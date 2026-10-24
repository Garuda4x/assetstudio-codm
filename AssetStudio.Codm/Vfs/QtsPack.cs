using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetStudio.Codm.Vfs
{
    /// <summary>One file inside a Qts pack (PufferQts download pack or the ExtractQts patch pack).</summary>
    public sealed class QtsEntry
    {
        public QtsPack Pack { get; internal set; }
        /// <summary>Name as stored: "dynamic/module/&lt;cipher&gt;.pak", "dynamic/split/&lt;md5&gt;.cpak", "split/&lt;md5&gt;.cpak"...</summary>
        public string Name { get; internal set; }
        public uint Size { get; internal set; }
        public int Ordinal { get; internal set; }
        /// <summary>Global logical page: part = page / pagesPerPart, the remainder goes through the part's page map.</summary>
        public uint LogicalPage { get; internal set; }
        public int BlockCount { get; internal set; }
        public int LastBlockSize { get; internal set; }
        /// <summary>End offsets (relative to the entry's first data byte) of blocks 0..n-2; the last block ends where its LZ4 stream ends.</summary>
        public uint[] BlockEnds { get; internal set; }
        public byte[] Hash { get; internal set; }

        public string FileName => Name.Substring(Name.LastIndexOf('/') + 1);
        public bool IsChunk => Name.EndsWith(".cpak", StringComparison.OrdinalIgnoreCase);
        /// <summary>md5 (lower case) for chunk entries, null otherwise.</summary>
        public string ChunkId => IsChunk ? System.IO.Path.GetFileNameWithoutExtension(FileName).ToLowerInvariant() : null;
        public int Part => (int)(LogicalPage / (uint)Pack.PagesPerPart);
    }

    /// <summary>
    /// Reader for Tencent's Qts VFS packs as used by CoD Mobile 1.9.57: an index (AssetBundle.pak, magic
    /// ED FF 34 01) plus data parts (AssetBundle_&lt;k&gt;.pak: 32-byte header, one flag byte per page, a
    /// u32 page map, then 4 KB data pages). Entries are LZ4 blocks of 16 KB raw, back to back.
    /// </summary>
    public sealed class QtsPack : IDisposable
    {
        public const int PageSize = 4096;
        public const int BlockSize = 16384;
        private const uint IndexMagic = 0x0134FFED;
        private const uint DescriptorMagic = 0x01343E98;

        public string Directory { get; private set; }
        public string IndexPath { get; private set; }
        public string Label { get; private set; }
        public int PagesPerPart { get; private set; } = 128000;
        public int PartCount { get; private set; }
        public uint[] PartStamps { get; private set; }
        public List<QtsEntry> Entries { get; } = new List<QtsEntry>();

        private readonly Dictionary<string, QtsEntry> byName = new Dictionary<string, QtsEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, QtsEntry> byChunk = new Dictionary<string, QtsEntry>(StringComparer.OrdinalIgnoreCase);
        private Part[] parts;
        private readonly object gate = new object();

        private sealed class Part
        {
            public FileStream Stream;
            public uint[] Map;
            public long DataStart;
            public long Length;
        }

        public int PartsOnDisk => Enumerable.Range(0, PartCount).Count(k => File.Exists(PartPath(k)));
        public string PartPath(int k) => System.IO.Path.Combine(Directory, $"AssetBundle_{k}.pak");

        public static bool LooksLikeIndex(string path)
        {
            if (!File.Exists(path)) return false;
            using (var fs = File.OpenRead(path))
            {
                var b = new byte[4];
                return fs.Read(b, 0, 4) == 4 && BitConverter.ToUInt32(b, 0) == IndexMagic;
            }
        }

        public static QtsPack Open(string directory, string label, Action<string> log = null)
        {
            var pack = new QtsPack
            {
                Directory = directory,
                IndexPath = System.IO.Path.Combine(directory, "AssetBundle.pak"),
                Label = label,
            };
            pack.ParseIndex(File.ReadAllBytes(pack.IndexPath));
            pack.parts = new Part[pack.PartCount];
            log?.Invoke($"{label}: {pack.Entries.Count} entries ({pack.byChunk.Count} chunks), {pack.PartCount} parts ({pack.PartsOnDisk} on disk) in {directory}");
            return pack;
        }

        private static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
        private static ushort U16(byte[] b, int o) => BitConverter.ToUInt16(b, o);

        private void ParseIndex(byte[] d)
        {
            if (U32(d, 0) != IndexMagic)
                throw new InvalidDataException("not a Qts index: " + IndexPath);
            var pageSize = (int)U32(d, 8);
            PagesPerPart = (int)U32(d, 12);
            var sectionOff = (int)U32(d, 16);
            var dirOff = (int)U32(d, 24);
            var tableCount = (int)U32(d, 28);
            PartCount = (int)U32(d, 32);
            var descOff = (int)U32(d, 36);
            if (pageSize != PageSize)
                throw new InvalidDataException($"unexpected page size {pageSize}");

            PartStamps = new uint[PartCount];
            for (var i = 0; i < PartCount; i++)
            {
                var o = descOff + 16 * i;
                if (U32(d, o) != DescriptorMagic)
                    throw new InvalidDataException($"bad part descriptor {i}");
                PartStamps[i] = U32(d, o + 4);
            }

            var tables = new (int off, int size)[tableCount];
            for (var i = 0; i < tableCount; i++)
                tables[i] = ((int)U32(d, dirOff + 8 * i), (int)U32(d, dirOff + 8 * i + 4));

            // QTSF node section: header, first record at +sec[4], length sec[3]
            var secLen = (int)U32(d, sectionOff + 12);
            var pos = sectionOff + (int)U32(d, sectionOff + 16);
            var end = Math.Min(d.Length, sectionOff + secLen);
            while (pos + 48 <= end)
            {
                var self = U32(d, pos);
                var neg = U32(d, pos + 4);
                var nodeSize = (int)U32(d, pos + 8);
                if (self != (uint)(pos - sectionOff) || neg != 0xFFFFFFFF || nodeSize < 48)
                {
                    pos += 4;
                    continue;
                }
                var nameLen = (int)U32(d, pos + 20);
                var size = U32(d, pos + 28);
                var ordinal = (int)U32(d, pos + 32);
                var q = pos + 48;
                if (nameLen > 0 && nameLen < 2048 && q + nameLen <= d.Length && ordinal >= 0 && ordinal < tableCount)
                {
                    var name = Encoding.ASCII.GetString(d, q, nameLen);
                    var e = new QtsEntry { Pack = this, Name = name, Size = size, Ordinal = ordinal };
                    var (toff, tsize) = tables[ordinal];
                    e.BlockCount = U16(d, toff);
                    e.LastBlockSize = U16(d, toff + 2);
                    var n = Math.Max(0, e.BlockCount - 1);
                    e.BlockEnds = new uint[n];
                    for (var i = 0; i < n; i++)
                        e.BlockEnds[i] = U32(d, toff + 8 + 4 * i);
                    var tuple = toff + 8 + 4 * n;
                    e.LogicalPage = U32(d, tuple + 8);
                    e.Hash = new byte[16];
                    Buffer.BlockCopy(d, toff + tsize - 16, e.Hash, 0, 16);
                    Entries.Add(e);
                    byName[name] = e;
                    if (e.IsChunk) byChunk[e.ChunkId] = e;
                }
                pos += nodeSize;
                pos += (4 - pos % 4) % 4;
            }
        }

        public bool TryGet(string name, out QtsEntry entry) => byName.TryGetValue(name, out entry);
        public bool TryGetChunk(string md5, out QtsEntry entry) => byChunk.TryGetValue(md5, out entry);

        private Part OpenPart(int k)
        {
            lock (gate)
            {
                if (parts[k] != null) return parts[k];
                var path = PartPath(k);
                if (!File.Exists(path))
                    throw new FileNotFoundException($"{Label}: part {k} is not on disk", path);
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                var hdr = new byte[32];
                ReadExactly(fs, hdr, 0, 32);
                var pageSize = (int)U32(hdr, 16);
                var pages = (int)U32(hdr, 20);
                if (pageSize != PageSize || pages != PagesPerPart)
                    throw new InvalidDataException($"{path}: unexpected part geometry {pageSize} x {pages}");
                fs.Position = 32 + pages;
                var raw = new byte[pages * 4];
                ReadExactly(fs, raw, 0, raw.Length);
                var map = new uint[pages];
                Buffer.BlockCopy(raw, 0, map, 0, raw.Length);
                parts[k] = new Part { Stream = fs, Map = map, DataStart = 32 + pages + 4L * pages, Length = fs.Length };
                return parts[k];
            }
        }

        private static void ReadExactly(Stream s, byte[] b, int off, int count)
        {
            while (count > 0)
            {
                var n = s.Read(b, off, count);
                if (n <= 0) throw new EndOfStreamException();
                off += n;
                count -= n;
            }
        }

        /// <summary>Decompress a whole entry.</summary>
        public byte[] Read(QtsEntry e)
        {
            if (e.Size == 0) return new byte[0];
            var part = OpenPart(e.Part);
            var local = (int)(e.LogicalPage % (uint)PagesPerPart);
            var phys = part.Map[local];
            var offset = part.DataStart + (long)phys * PageSize;
            var bound = (e.BlockEnds.Length > 0 ? (long)e.BlockEnds[e.BlockEnds.Length - 1] : 0) + BlockSize + PageSize;
            var toRead = (int)Math.Min(bound, part.Length - offset);
            var src = new byte[toRead];
            lock (gate)
            {
                part.Stream.Position = offset;
                ReadExactly(part.Stream, src, 0, toRead);
            }
            var dst = new byte[e.Size];
            var op = 0;
            var ip = 0;
            for (var b = 0; b < e.BlockCount; b++)
            {
                var raw = Math.Min(BlockSize, dst.Length - op);
                if (b < e.BlockEnds.Length)
                {
                    var end = (int)e.BlockEnds[b];
                    var used = Lz4Block.Decode(src, ip, Math.Min(end, src.Length), dst, op, op + raw);
                    ip = end;
                }
                else
                {
                    Lz4Block.Decode(src, ip, src.Length, dst, op, op + raw);
                }
                op += raw;
            }
            if (op != dst.Length)
                throw new InvalidDataException($"{Label}: {e.Name}: decoded {op} of {dst.Length} bytes");
            return dst;
        }

        public void Dispose()
        {
            if (parts == null) return;
            foreach (var p in parts)
                p?.Stream?.Dispose();
        }
    }
}
