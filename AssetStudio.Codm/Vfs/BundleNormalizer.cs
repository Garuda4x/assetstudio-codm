using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AssetStudio.Codm.Vfs
{
    /// <summary>
    /// Rewrites a CoD Mobile 1.9.57 bundle into a stock Unity 5.6 bundle: the serialized files inside use
    /// format 18 with a list of "qtc:/split/&lt;md5&gt;.cpak" chunk references and object entries whose payload
    /// may live in one of those chunks. The normalizer fetches the chunks, appends the external payloads to
    /// the data section, writes the object table in the stock 17 layout and re-emits an uncompressed UnityFS.
    /// </summary>
    public static class BundleNormalizer
    {
        public sealed class Stats
        {
            public int SerializedFiles;
            public int RewrittenFiles;
            public int Objects;
            public int ExternalObjects;
            public long ExternalBytes;
            public HashSet<string> Chunks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class Node
        {
            public long Offset;
            public long Size;
            public uint Flags;
            public string Path;
            public byte[] Data;
        }

        public static bool IsUnityFS(byte[] b) =>
            b != null && b.Length > 8 && b[0] == (byte)'U' && b[1] == (byte)'n' && b[2] == (byte)'i' && b[3] == (byte)'t' && b[4] == (byte)'y' && b[5] == (byte)'F' && b[6] == (byte)'S' && b[7] == 0;

        /// <summary>
        /// Returns the normalized bundle, or null when nothing needed rewriting (the caller keeps the original).
        /// <paramref name="resolveChunk"/> receives the md5 of a chunk and returns its bytes (or null).
        /// </summary>
        public static byte[] Normalize(byte[] bundle, Func<string, byte[]> resolveChunk, Stats stats = null, Action<string> log = null)
        {
            if (!IsUnityFS(bundle))
                return null;
            stats = stats ?? new Stats();
            var r = new BigEndianReader(bundle);
            r.ReadCString(); // UnityFS
            var version = r.ReadUInt32();
            var unityVersion = r.ReadCString();
            var unityRevision = r.ReadCString();
            var totalSize = r.ReadInt64();
            var cbi = (int)r.ReadUInt32();
            var ubi = (int)r.ReadUInt32();
            var flags = r.ReadUInt32();
            if (version != 6)
                throw new NotSupportedException($"UnityFS version {version}");
            byte[] blocksInfo;
            int dataPos;
            if ((flags & 0x80) != 0)
            {
                blocksInfo = Decompress(bundle, bundle.Length - cbi, cbi, ubi, flags & 0x3F);
                dataPos = r.Position;
            }
            else
            {
                blocksInfo = Decompress(bundle, r.Position, cbi, ubi, flags & 0x3F);
                dataPos = r.Position + cbi;
            }
            var bi = new BigEndianReader(blocksInfo);
            bi.Position = 16; // hash
            var blockCount = bi.ReadInt32();
            var blocks = new (uint u, uint c, ushort f)[blockCount];
            for (var i = 0; i < blockCount; i++)
                blocks[i] = (bi.ReadUInt32(), bi.ReadUInt32(), bi.ReadUInt16());
            var nodeCount = bi.ReadInt32();
            var nodes = new List<Node>(nodeCount);
            for (var i = 0; i < nodeCount; i++)
                nodes.Add(new Node { Offset = bi.ReadInt64(), Size = bi.ReadInt64(), Flags = bi.ReadUInt32(), Path = bi.ReadCString() });

            long dataTotal = 0;
            foreach (var b in blocks) dataTotal += b.u;
            var data = new byte[dataTotal];
            var dp = 0;
            var sp = dataPos;
            foreach (var (u, c, f) in blocks)
            {
                var dec = Decompress(bundle, sp, (int)c, (int)u, (uint)(f & 0x3F));
                Buffer.BlockCopy(dec, 0, data, dp, (int)u);
                dp += (int)u;
                sp += (int)c;
            }

            var rewritten = 0;
            foreach (var n in nodes)
            {
                var slice = new byte[n.Size];
                Buffer.BlockCopy(data, (int)n.Offset, slice, 0, (int)n.Size);
                if (IsSerializedFile(slice))
                {
                    stats.SerializedFiles++;
                    var rewrittenFile = RewriteSerializedFile(slice, resolveChunk, stats, log);
                    if (rewrittenFile != null)
                    {
                        slice = rewrittenFile;
                        rewritten++;
                        stats.RewrittenFiles++;
                    }
                }
                n.Data = slice;
            }
            if (rewritten == 0)
                return null;
            return EmitUnityFS(unityVersion, unityRevision, nodes);
        }

        private static byte[] Decompress(byte[] src, int off, int compressed, int uncompressed, uint type)
        {
            switch (type)
            {
                case 0:
                {
                    var o = new byte[uncompressed];
                    Buffer.BlockCopy(src, off, o, 0, uncompressed);
                    return o;
                }
                case 2:
                case 3:
                {
                    var o = new byte[uncompressed];
                    Lz4Block.Decode(src, off, off + compressed, o, 0, uncompressed);
                    return o;
                }
                default:
                    throw new NotSupportedException($"bundle block compression {type}");
            }
        }

        private static bool IsSerializedFile(byte[] b)
        {
            if (b.Length < 32) return false;
            var r = new BigEndianReader(b);
            var metadataSize = r.ReadUInt32();
            var fileSize = r.ReadUInt32();
            var version = r.ReadUInt32();
            var dataOffset = r.ReadUInt32();
            return fileSize == b.Length && version >= 9 && version <= 22 && dataOffset <= fileSize && metadataSize < fileSize;
        }

        private sealed class ObjectEntry
        {
            public long PathId;
            public bool External;
            public uint ByteStart;   // local
            public uint RefIndex;    // external
            public uint ChunkOffset; // external
            public uint ByteSize;
            public int TypeId;
        }

        /// <summary>Rewrite one format-18 serialized file into format 17 with all payloads local; null if not format 18.</summary>
        private static byte[] RewriteSerializedFile(byte[] sf, Func<string, byte[]> resolveChunk, Stats stats, Action<string> log)
        {
            var be = new BigEndianReader(sf);
            var metadataSize = be.ReadUInt32();
            var fileSize = be.ReadUInt32();
            var version = be.ReadUInt32();
            var dataOffset = be.ReadUInt32();
            var endian = sf[16];
            if (version != 18 || endian != 0)
                return null;
            var p = 20;
            var unityVersionEnd = IndexOfZero(sf, p);
            var unityVersion = Sub(sf, p, unityVersionEnd - p);
            p = unityVersionEnd + 1;
            var platform = Sub(sf, p, 4); p += 4;
            var enableTypeTree = sf[p]; p += 1;
            var refCount = BitConverter.ToInt32(sf, p); p += 4;
            if (refCount < 0 || refCount > 4096)
                return null;
            var refs = new List<string>(refCount);
            for (var i = 0; i < refCount; i++)
            {
                var e = IndexOfZero(sf, p);
                refs.Add(Encoding.ASCII.GetString(sf, p, e - p));
                p = e + 1;
            }
            if (refCount > 0 && !refs[0].StartsWith("qtc:", StringComparison.Ordinal))
                return null;

            // type list (copied verbatim)
            var typesStart = p;
            var typeCount = BitConverter.ToInt32(sf, p); p += 4;
            for (var i = 0; i < typeCount; i++)
            {
                var classId = BitConverter.ToInt32(sf, p); p += 4;
                p += 1; // stripped
                p += 2; // script type index
                if (classId == 114 || classId < 0) p += 16;
                p += 16;
                if (enableTypeTree != 0)
                {
                    var nodeCount = BitConverter.ToInt32(sf, p); p += 4;
                    var stringSize = BitConverter.ToInt32(sf, p); p += 4;
                    p += nodeCount * 24 + stringSize;
                }
            }
            var typesEnd = p;

            // object table (variable length)
            var objectCount = BitConverter.ToInt32(sf, p); p += 4;
            var objects = new List<ObjectEntry>(objectCount);
            for (var i = 0; i < objectCount; i++)
            {
                p += (4 - p % 4) % 4;
                var o = new ObjectEntry { PathId = BitConverter.ToInt64(sf, p) };
                var flag = BitConverter.ToUInt32(sf, p + 8);
                if (flag == 0)
                {
                    o.ByteStart = BitConverter.ToUInt32(sf, p + 12);
                    o.ByteSize = BitConverter.ToUInt32(sf, p + 16);
                    o.TypeId = BitConverter.ToInt32(sf, p + 20);
                    p += 24;
                }
                else if (flag == 1)
                {
                    o.External = true;
                    o.RefIndex = BitConverter.ToUInt32(sf, p + 12);
                    o.ChunkOffset = BitConverter.ToUInt32(sf, p + 16);
                    o.ByteSize = BitConverter.ToUInt32(sf, p + 20);
                    o.TypeId = BitConverter.ToInt32(sf, p + 24);
                    p += 28;
                }
                else
                {
                    throw new InvalidDataException($"format 18: unknown object flag {flag} at object {i}");
                }
                objects.Add(o);
            }

            // script types (copied verbatim)
            var scriptsStart = p;
            var scriptCount = BitConverter.ToInt32(sf, p); p += 4;
            for (var i = 0; i < scriptCount; i++)
            {
                p += 4;
                p += (4 - p % 4) % 4;
                p += 8;
            }
            // externals (copied verbatim)
            var externalCount = BitConverter.ToInt32(sf, p); p += 4;
            for (var i = 0; i < externalCount; i++)
            {
                p = IndexOfZero(sf, p) + 1; // temp empty
                p += 16 + 4;                // guid, type
                p = IndexOfZero(sf, p) + 1; // path
            }
            var userInfoEnd = IndexOfZero(sf, p) + 1;
            var tailStart = scriptsStart;
            var tailLen = userInfoEnd - scriptsStart;

            // data
            var dataLen = (int)(fileSize - dataOffset);
            var appended = new MemoryStream();
            var chunkCache = new Dictionary<int, byte[]>();
            foreach (var o in objects)
            {
                stats.Objects++;
                if (!o.External) continue;
                stats.ExternalObjects++;
                if (!chunkCache.TryGetValue((int)o.RefIndex, out var chunk))
                {
                    var refName = refs[(int)o.RefIndex];
                    var md5 = Path.GetFileNameWithoutExtension(refName);
                    chunk = resolveChunk(md5) ?? throw new FileNotFoundException($"chunk {refName} not found in any pack");
                    chunkCache[(int)o.RefIndex] = chunk;
                    stats.Chunks.Add(md5);
                }
                if (o.ChunkOffset + (long)o.ByteSize > chunk.Length)
                    throw new InvalidDataException($"object {o.PathId}: chunk slice {o.ChunkOffset}+{o.ByteSize} exceeds chunk size {chunk.Length}");
                var pad = (8 - (int)((dataLen + appended.Length) % 8)) % 8;
                for (var k = 0; k < pad; k++) appended.WriteByte(0);
                o.ByteStart = (uint)(dataLen + appended.Length);
                appended.Write(chunk, (int)o.ChunkOffset, (int)o.ByteSize);
                stats.ExternalBytes += o.ByteSize;
            }

            // write format 17
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            ms.Position = 20; // header later
            w.Write(unityVersion); w.Write((byte)0);
            w.Write(platform);
            w.Write(enableTypeTree);
            w.Write(sf, typesStart, typesEnd - typesStart);
            w.Write(objectCount);
            foreach (var o in objects)
            {
                Align(ms, 4);
                w.Write(o.PathId);
                w.Write(o.ByteStart);
                w.Write(o.ByteSize);
                w.Write(o.TypeId);
            }
            w.Write(sf, tailStart, tailLen);
            var metadataEnd = (uint)ms.Position;
            Align(ms, 16);
            var newDataOffset = (uint)ms.Position;
            w.Write(sf, (int)dataOffset, dataLen);
            appended.Position = 0;
            appended.CopyTo(ms);
            var newFileSize = (uint)ms.Length;
            ms.Position = 0;
            WriteBE(w, metadataEnd - 20);
            WriteBE(w, newFileSize);
            WriteBE(w, 17u);
            WriteBE(w, newDataOffset);
            w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);
            return ms.ToArray();
        }

        private static byte[] EmitUnityFS(string unityVersion, string unityRevision, List<Node> nodes)
        {
            long dataTotal = 0;
            foreach (var n in nodes) dataTotal += n.Data.Length;

            var info = new MemoryStream();
            var iw = new BinaryWriter(info);
            iw.Write(new byte[16]);
            WriteBE(iw, 1);
            WriteBE(iw, (uint)dataTotal);
            WriteBE(iw, (uint)dataTotal);
            iw.Write((byte)0); iw.Write((byte)0); // block flags: no compression
            WriteBE(iw, nodes.Count);
            long off = 0;
            foreach (var n in nodes)
            {
                WriteBE(iw, off);
                WriteBE(iw, (long)n.Data.Length);
                WriteBE(iw, n.Flags);
                iw.Write(Encoding.UTF8.GetBytes(n.Path)); iw.Write((byte)0);
                off += n.Data.Length;
            }
            var infoBytes = info.ToArray();

            var header = new MemoryStream();
            var hw = new BinaryWriter(header);
            hw.Write(Encoding.ASCII.GetBytes("UnityFS")); hw.Write((byte)0);
            WriteBE(hw, 6u);
            hw.Write(Encoding.ASCII.GetBytes(unityVersion)); hw.Write((byte)0);
            hw.Write(Encoding.ASCII.GetBytes(unityRevision)); hw.Write((byte)0);
            var headerLen = header.Length + 8 + 4 + 4 + 4;
            WriteBE(hw, headerLen + infoBytes.Length + dataTotal);
            WriteBE(hw, (uint)infoBytes.Length);
            WriteBE(hw, (uint)infoBytes.Length);
            WriteBE(hw, 0x40u); // blocks and directory info combined, uncompressed

            var outStream = new MemoryStream();
            header.Position = 0; header.CopyTo(outStream);
            outStream.Write(infoBytes, 0, infoBytes.Length);
            foreach (var n in nodes) outStream.Write(n.Data, 0, n.Data.Length);
            return outStream.ToArray();
        }

        private static int IndexOfZero(byte[] b, int from)
        {
            var i = Array.IndexOf(b, (byte)0, from);
            if (i < 0) throw new InvalidDataException("unterminated string");
            return i;
        }

        private static byte[] Sub(byte[] b, int off, int len)
        {
            var o = new byte[len];
            Buffer.BlockCopy(b, off, o, 0, len);
            return o;
        }

        private static void Align(Stream s, int n)
        {
            var pad = (n - (int)(s.Position % n)) % n;
            for (var k = 0; k < pad; k++) s.WriteByte(0);
        }

        private static void WriteBE(BinaryWriter w, uint v) { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v); }
        private static void WriteBE(BinaryWriter w, int v) => WriteBE(w, (uint)v);
        private static void WriteBE(BinaryWriter w, long v) { WriteBE(w, (uint)(v >> 32)); WriteBE(w, (uint)v); }

        /// <summary>Minimal big-endian reader over a byte array (bundle headers are big endian).</summary>
        private sealed class BigEndianReader
        {
            private readonly byte[] b;
            public int Position;
            public BigEndianReader(byte[] data) { b = data; }
            public uint ReadUInt32() { var v = (uint)(b[Position] << 24 | b[Position + 1] << 16 | b[Position + 2] << 8 | b[Position + 3]); Position += 4; return v; }
            public int ReadInt32() => (int)ReadUInt32();
            public ushort ReadUInt16() { var v = (ushort)(b[Position] << 8 | b[Position + 1]); Position += 2; return v; }
            public long ReadInt64() { var hi = (long)ReadUInt32(); var lo = (long)ReadUInt32(); return hi << 32 | lo; }
            public string ReadCString()
            {
                var e = Array.IndexOf(b, (byte)0, Position);
                var s = Encoding.UTF8.GetString(b, Position, e - Position);
                Position = e + 1;
                return s;
            }
        }
    }
}
