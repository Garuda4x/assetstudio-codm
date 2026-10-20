using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using K4os.Compression.LZ4;

namespace AssetStudio.Codm
{
    /// <summary>
    /// Reads only the UnityFS header and the (small, compressed) blocks-info directory of a bundle to
    /// learn which serialized files (CAB-...) it contains, without decompressing the data blocks.
    /// Used to build the CAB -> pak index for dependency resolution.
    /// </summary>
    public static class BundleHeaderReader
    {
        public sealed class Info
        {
            public string Signature { get; set; }
            public uint Version { get; set; }
            public string UnityVersion { get; set; }
            public string UnityRevision { get; set; }
            public long Size { get; set; }
            public List<string> Entries { get; } = new List<string>();
        }

        public static Info Read(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024))
            using (var reader = new EndianBinaryReader(fs))
            {
                var info = new Info { Signature = reader.ReadStringToNull() };
                if (info.Signature != "UnityFS")
                    throw new InvalidDataException($"not a UnityFS bundle: {info.Signature}");
                info.Version = reader.ReadUInt32();
                info.UnityVersion = reader.ReadStringToNull();
                info.UnityRevision = reader.ReadStringToNull();
                info.Size = reader.ReadInt64();
                var compressedBlocksInfoSize = reader.ReadUInt32();
                var uncompressedBlocksInfoSize = reader.ReadUInt32();
                var flags = (ArchiveFlags)reader.ReadUInt32();

                if (info.Version >= 7)
                    reader.AlignStream(16);

                byte[] blocksInfoBytes;
                if ((flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0)
                {
                    fs.Position = fs.Length - compressedBlocksInfoSize;
                    blocksInfoBytes = reader.ReadBytes((int)compressedBlocksInfoSize);
                }
                else
                {
                    blocksInfoBytes = reader.ReadBytes((int)compressedBlocksInfoSize);
                }

                byte[] uncompressed;
                switch ((CompressionType)(flags & ArchiveFlags.CompressionTypeMask))
                {
                    case CompressionType.None:
                        uncompressed = blocksInfoBytes;
                        break;
                    case CompressionType.Lzma:
                    {
                        var outStream = new MemoryStream((int)uncompressedBlocksInfoSize);
                        using (var inStream = new MemoryStream(blocksInfoBytes))
                            SevenZipHelper.StreamDecompress(inStream, outStream, compressedBlocksInfoSize, uncompressedBlocksInfoSize);
                        uncompressed = outStream.ToArray();
                        break;
                    }
                    case CompressionType.Lz4:
                    case CompressionType.Lz4HC:
                    {
                        uncompressed = new byte[uncompressedBlocksInfoSize];
                        var n = LZ4Codec.Decode(blocksInfoBytes, uncompressed);
                        if (n != uncompressedBlocksInfoSize)
                            throw new InvalidDataException($"LZ4 blocks info decode wrote {n} of {uncompressedBlocksInfoSize} bytes");
                        break;
                    }
                    default:
                        throw new InvalidDataException("unsupported blocks info compression");
                }

                using (var br = new EndianBinaryReader(new MemoryStream(uncompressed)))
                {
                    br.ReadBytes(16); // uncompressed data hash
                    var blockCount = br.ReadInt32();
                    for (var i = 0; i < blockCount; i++)
                    {
                        br.ReadUInt32();
                        br.ReadUInt32();
                        br.ReadUInt16();
                    }
                    var nodeCount = br.ReadInt32();
                    for (var i = 0; i < nodeCount; i++)
                    {
                        br.ReadInt64();
                        br.ReadInt64();
                        br.ReadUInt32();
                        info.Entries.Add(br.ReadStringToNull());
                    }
                }
                return info;
            }
        }
    }
}
