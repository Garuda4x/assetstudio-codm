using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetStudio.Codm.Vfs
{
    /// <summary>One file inside the base pack (the former StreamingAssets bundles plus the shared chunks).</summary>
    public sealed class QtsfEntry
    {
        public QtsfPack Pack { get; internal set; }
        public ulong Id { get; internal set; }
        /// <summary>Name as stored: the ciphered pak name ("LUJ_EUJMCF$...pak") or "&lt;md5&gt;.cpak".</summary>
        public string Name { get; internal set; }
        public uint Size { get; internal set; }
        public int Part { get; internal set; }
        public byte[] Hash20 { get; internal set; }
        /// <summary>Offset of the entry's first node inside its part file; -1 until the offset cache exists.</summary>
        public long Offset { get; internal set; } = -1;
        /// <summary>Sum of the raw block sizes found while walking; differs from Size for a handful of entries.</summary>
        public long StoredSize { get; internal set; } = -1;

        public string FileName => Name;
        public bool IsChunk => Name.EndsWith(".cpak", StringComparison.OrdinalIgnoreCase);
        public string ChunkId => IsChunk ? System.IO.Path.GetFileNameWithoutExtension(Name).ToLowerInvariant() : null;
    }

    /// <summary>
    /// Reader for the QTSF base pack of CoD Mobile 1.9.57 (StreamingAssets\AssetBundle\base): an index
    /// (AssetBundle_base.pak), a pack-order file and 2 GB data parts holding one node per 16 KB block
    /// (node header, block header, zstd frame). The index has no offsets, so a one-time walk over the
    /// parts assigns each entry its first node; entries follow the pack order inside a part.
    /// </summary>
    public sealed class QtsfPack : IDisposable
    {
        public const int BlockSize = 16384;
        private const int NodeHeaderSize = 36; // 20 node header + 16 block header
        private static readonly byte[] IndexMagic = { 0x01, 0x00, 0x00, 0x01, 0x01, 0x02, 0x03, 0x04 };
        private const string OffsetCacheMagic = "QOF1";

        public string Directory { get; private set; }
        public string IndexPath { get; private set; }
        public string Prefix { get; private set; }
        public string Label { get; private set; }
        public int PartCount { get; private set; }
        public List<QtsfEntry> Entries { get; } = new List<QtsfEntry>();
        public ulong[] PackOrder { get; private set; }

        private readonly Dictionary<string, QtsfEntry> byName = new Dictionary<string, QtsfEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, QtsfEntry> byChunk = new Dictionary<string, QtsfEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ulong, QtsfEntry> byId = new Dictionary<ulong, QtsfEntry>();
        private readonly object gate = new object();
        private FileStream[] parts;

        public bool HasOffsets => Entries.Count > 0 && Entries.All(e => e.Offset >= 0);
        public int EntriesWithOffsets => Entries.Count(e => e.Offset >= 0);
        public string PartPath(int k) => System.IO.Path.Combine(Directory, $"{Prefix}_{k}.pak");

        public static bool LooksLikeIndex(string path)
        {
            if (!File.Exists(path)) return false;
            using (var fs = File.OpenRead(path))
            {
                var b = new byte[8];
                return fs.Read(b, 0, 8) == 8 && b.SequenceEqual(IndexMagic);
            }
        }

        public static QtsfPack Open(string directory, string indexFileName, string label, Action<string> log = null)
        {
            var pack = new QtsfPack
            {
                Directory = directory,
                IndexPath = System.IO.Path.Combine(directory, indexFileName),
                Prefix = System.IO.Path.GetFileNameWithoutExtension(indexFileName),
                Label = label,
            };
            pack.ParseIndex(File.ReadAllBytes(pack.IndexPath));
            var orderPath = System.IO.Path.Combine(directory, pack.Prefix + "_packorder.bin");
            pack.PackOrder = File.Exists(orderPath) ? ParsePackOrder(File.ReadAllBytes(orderPath)) : new ulong[0];
            var k = 0;
            while (File.Exists(pack.PartPath(k))) k++;
            pack.PartCount = k;
            pack.parts = new FileStream[k];
            log?.Invoke($"{label}: {pack.Entries.Count} entries ({pack.byChunk.Count} chunks), {pack.PartCount} parts in {directory}");
            return pack;
        }

        private static ulong[] ParsePackOrder(byte[] b)
        {
            if (b.Length < 12 || b[0] != 'Q' || b[1] != 'P' || b[2] != 'K' || b[3] != 'O')
                throw new InvalidDataException("not a packorder file");
            var count = BitConverter.ToInt32(b, 8);
            var ids = new ulong[count];
            for (var i = 0; i < count; i++)
                ids[i] = BitConverter.ToUInt64(b, 12 + 8 * i);
            return ids;
        }

        private void ParseIndex(byte[] d)
        {
            if (d.Length < 8 || !d.Take(8).SequenceEqual(IndexMagic))
                throw new InvalidDataException("not a QTSF index: " + IndexPath);
            for (var p = 32; p + 48 < d.Length; p++)
            {
                if (d[p] != 'Q' || d[p + 1] != 'T' || d[p + 2] != 'S' || d[p + 3] != 'F')
                    continue;
                if (d[p + 4] == '_' && d[p + 5] == 'P' && d[p + 6] == 'A' && d[p + 7] == 'C') // QTSF_PACKAGE marker (an id may start with '_')
                    continue;
                var a = BitConverter.ToUInt32(d, p - 12);
                var payloadSize = (int)BitConverter.ToUInt32(d, p - 8);
                if (a != 8 || payloadSize < 40 || p + payloadSize > d.Length)
                    continue;
                var nameLen = (int)BitConverter.ToUInt32(d, p + 20);
                if (nameLen <= 0 || nameLen > 1024 || 24 + nameLen + 36 > payloadSize + 8)
                    continue;
                var e = new QtsfEntry
                {
                    Pack = this,
                    Id = BitConverter.ToUInt64(d, p + 4),
                    Name = Encoding.ASCII.GetString(d, p + 24, nameLen),
                };
                var r = 24 + nameLen;
                r += (4 - r % 4) % 4;
                e.Size = BitConverter.ToUInt32(d, p + r);
                e.Part = (int)BitConverter.ToUInt32(d, p + r + 4);
                e.Hash20 = new byte[20];
                Buffer.BlockCopy(d, p + r + 8, e.Hash20, 0, 20);
                if (e.Part > 4096)
                    continue;
                Entries.Add(e);
                byName[e.Name] = e;
                byId[e.Id] = e;
                if (e.IsChunk) byChunk[e.ChunkId] = e;
                p += 3; // records are packed tighter than their payload size suggests; keep scanning
            }
        }

        public bool TryGet(string name, out QtsfEntry entry) => byName.TryGetValue(name, out entry);
        public bool TryGetChunk(string md5, out QtsfEntry entry) => byChunk.TryGetValue(md5, out entry);

        // ------------------------------------------------------------------ offsets

        /// <summary>Load the offset cache written by <see cref="BuildOffsets"/>; false when missing or stale.</summary>
        public bool LoadOffsets(string cachePath)
        {
            if (!File.Exists(cachePath)) return false;
            try
            {
                using (var r = new BinaryReader(File.OpenRead(cachePath)))
                {
                    if (Encoding.ASCII.GetString(r.ReadBytes(4)) != OffsetCacheMagic) return false;
                    var partCount = r.ReadInt32();
                    if (partCount != PartCount) return false;
                    for (var k = 0; k < partCount; k++)
                        if (r.ReadInt64() != new FileInfo(PartPath(k)).Length) return false;
                    var count = r.ReadInt32();
                    var loaded = 0;
                    for (var i = 0; i < count; i++)
                    {
                        var id = r.ReadUInt64();
                        var part = r.ReadInt32();
                        var offset = r.ReadInt64();
                        var stored = r.ReadInt64();
                        if (byId.TryGetValue(id, out var e) && e.Part == part)
                        {
                            e.Offset = offset;
                            e.StoredSize = stored;
                            loaded++;
                        }
                    }
                    return loaded > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private void SaveOffsets(string cachePath)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cachePath));
            using (var w = new BinaryWriter(File.Create(cachePath)))
            {
                w.Write(Encoding.ASCII.GetBytes(OffsetCacheMagic));
                w.Write(PartCount);
                for (var k = 0; k < PartCount; k++)
                    w.Write(new FileInfo(PartPath(k)).Length);
                var with = Entries.Where(e => e.Offset >= 0).ToList();
                w.Write(with.Count);
                foreach (var e in with)
                {
                    w.Write(e.Id);
                    w.Write(e.Part);
                    w.Write(e.Offset);
                    w.Write(e.StoredSize);
                }
            }
        }

        /// <summary>
        /// Walk every part once: nodes are visited in order, entries follow the pack order inside a part,
        /// an entry ends with a block shorter than 16 KB. Writes the cache when done.
        /// </summary>
        public void BuildOffsets(string cachePath, Action<string> progress = null)
        {
            var mismatches = 0;
            for (var k = 0; k < PartCount; k++)
            {
                var expected = PackOrder.Select(id => byId.TryGetValue(id, out var e) ? e : null).Where(e => e != null && e.Part == k).ToList();
                progress?.Invoke($"{Label}: walking part {k}/{PartCount - 1} ({expected.Count} entries)...");
                using (var walker = new PartWalker(PartPath(k)))
                {
                    var pos = walker.FindNode(0x1000);
                    var done = 0;
                    foreach (var e in expected)
                    {
                        if (pos < 0) break;
                        long acc = 0;
                        var start = pos;
                        while (true)
                        {
                            if (!walker.TryReadHeader(pos, out var h))
                            {
                                var next = walker.FindNode(pos);
                                if (next < 0) { pos = -1; break; }
                                pos = next;
                                if (acc == 0) start = pos;
                                continue;
                            }
                            acc += h.RawSize;
                            pos += h.NodeSize;
                            if (h.RawSize < BlockSize || acc >= e.Size)
                                break;
                        }
                        e.Offset = start;
                        e.StoredSize = acc;
                        if (acc != e.Size)
                        {
                            mismatches++;
                            if (mismatches <= 5)
                                progress?.Invoke($"{Label}: part {k}: {e.Name} stored {acc} bytes, index says {e.Size}");
                        }
                        done++;
                        if (done % 2000 == 0)
                            progress?.Invoke($"{Label}: part {k}: {done}/{expected.Count} entries, {pos * 100.0 / walker.Length:F0}% of file");
                    }
                }
            }
            progress?.Invoke($"{Label}: offsets for {EntriesWithOffsets}/{Entries.Count} entries, {mismatches} size mismatches");
            SaveOffsets(cachePath);
        }

        private struct NodeHeader
        {
            public int NodeSize;
            public int CompressedSize;
            public int Method;
            public int RawSize;
        }

        /// <summary>Sequential reader over a part with a sliding 1 MB window.</summary>
        private sealed class PartWalker : IDisposable
        {
            private readonly FileStream fs;
            private readonly byte[] buf = new byte[1 << 20];
            private long bufStart = -1;
            private int bufLen;
            public long Length => fs.Length;

            public PartWalker(string path)
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            }

            private bool Ensure(long pos, int count)
            {
                if (pos < 0 || pos + count > fs.Length) return false;
                if (bufStart >= 0 && pos >= bufStart && pos + count <= bufStart + bufLen) return true;
                fs.Position = pos;
                bufLen = 0;
                while (bufLen < buf.Length)
                {
                    var n = fs.Read(buf, bufLen, buf.Length - bufLen);
                    if (n <= 0) break;
                    bufLen += n;
                }
                bufStart = pos;
                return bufLen >= count;
            }

            public bool TryReadHeader(long pos, out NodeHeader h)
            {
                h = default;
                if (!Ensure(pos, NodeHeaderSize)) return false;
                var o = (int)(pos - bufStart);
                var self = BitConverter.ToUInt32(buf, o);
                var neg = BitConverter.ToUInt32(buf, o + 4);
                var nodeSize = BitConverter.ToInt32(buf, o + 8);
                var hl = BitConverter.ToInt32(buf, o + 20);
                var cs = BitConverter.ToInt32(buf, o + 24);
                var m = BitConverter.ToInt32(buf, o + 28);
                var us = BitConverter.ToInt32(buf, o + 32);
                if (self != (uint)pos || neg != 0xFFFFFFFF || hl != 16 || (m != 0 && m != 2) || us < 0 || us > BlockSize || cs < 0 || cs + NodeHeaderSize > nodeSize + 16 || nodeSize < NodeHeaderSize)
                    return false;
                h = new NodeHeader { NodeSize = nodeSize, CompressedSize = cs, Method = m, RawSize = us };
                return true;
            }

            /// <summary>First valid node header at or after <paramref name="pos"/> (4-byte steps), or -1.</summary>
            public long FindNode(long pos)
            {
                pos += (4 - pos % 4) % 4;
                var limit = pos + (64L << 20);
                while (pos + NodeHeaderSize <= fs.Length && pos < limit)
                {
                    if (TryReadHeader(pos, out _)) return pos;
                    pos += 4;
                }
                return -1;
            }

            public byte[] ReadAt(long pos, int count)
            {
                var o = new byte[count];
                fs.Position = pos;
                var got = 0;
                while (got < count)
                {
                    var n = fs.Read(o, got, count - got);
                    if (n <= 0) throw new EndOfStreamException();
                    got += n;
                }
                return o;
            }

            public void Dispose() => fs.Dispose();
        }

        // ------------------------------------------------------------------ reading

        /// <summary>Decompress a whole entry (requires the offset cache).</summary>
        public byte[] Read(QtsfEntry e)
        {
            if (e.Offset < 0)
                throw new InvalidOperationException($"{Label}: entry offsets not built yet (run the base pack index)");
            if (e.Size == 0) return new byte[0];
            lock (gate)
            {
                using (var walker = new PartWalker(PartPath(e.Part)))
                {
                    var dst = new byte[e.Size];
                    var acc = 0;
                    var pos = e.Offset;
                    while (acc < dst.Length)
                    {
                        if (!walker.TryReadHeader(pos, out var h))
                        {
                            var next = walker.FindNode(pos);
                            if (next < 0) break;
                            pos = next;
                            continue;
                        }
                        var raw = Math.Min(h.RawSize, dst.Length - acc);
                        var payload = walker.ReadAt(pos + NodeHeaderSize, h.CompressedSize);
                        if (h.Method == 2)
                        {
                            var n = ZstdFrame.Decompress(payload, 0, payload.Length, dst, acc, raw);
                            if (n != raw)
                                throw new InvalidDataException($"{Label}: {e.Name}: block decoded to {n} bytes, expected {raw}");
                        }
                        else
                        {
                            Buffer.BlockCopy(payload, 0, dst, acc, raw);
                        }
                        acc += raw;
                        pos += h.NodeSize;
                        if (h.RawSize < BlockSize) break;
                    }
                    if (acc != dst.Length)
                        Array.Resize(ref dst, acc);
                    return dst;
                }
            }
        }

        public void Dispose()
        {
            if (parts == null) return;
            foreach (var p in parts) p?.Dispose();
        }
    }
}
