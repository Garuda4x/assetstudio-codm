using System;
using System.IO;
using ZstdSharp;

namespace AssetStudio.Codm.Vfs
{
    /// <summary>zstd frame helpers for the base pack (one frame per 16 KB block, 8 trailing bytes after each frame).</summary>
    internal static class ZstdFrame
    {
        public const uint Magic = 0xFD2FB528;

        [ThreadStatic] private static Decompressor decompressor;

        /// <summary>Length in bytes of the zstd frame starting at <paramref name="off"/>, or -1 if it is not a frame.</summary>
        public static int FrameLength(byte[] b, int off, int end)
        {
            if (off + 6 > end || BitConverter.ToUInt32(b, off) != Magic)
                return -1;
            int p = off + 4;
            int fhd = b[p++];
            int fcsFlag = fhd >> 6;
            bool singleSegment = ((fhd >> 5) & 1) != 0;
            bool checksum = ((fhd >> 2) & 1) != 0;
            int dictFlag = fhd & 3;
            if (!singleSegment) p++; // window descriptor
            p += dictFlag == 3 ? 4 : dictFlag; // dictionary id: 0,1,2,4 bytes
            p += fcsFlag == 0 ? (singleSegment ? 1 : 0) : fcsFlag == 1 ? 2 : fcsFlag == 2 ? 4 : 8;
            while (true)
            {
                if (p + 3 > end) return -1;
                int bh = b[p] | (b[p + 1] << 8) | (b[p + 2] << 16);
                p += 3;
                bool last = (bh & 1) != 0;
                int type = (bh >> 1) & 3;
                int size = bh >> 3;
                p += type == 1 ? 1 : size; // RLE block stores one byte
                if (p > end) return -1;
                if (last) break;
            }
            if (checksum) p += 4;
            return p - off;
        }

        /// <summary>Decompress one frame into <paramref name="dst"/>; returns the number of bytes written.</summary>
        public static int Decompress(byte[] src, int off, int len, byte[] dst, int dstOff, int dstLen)
        {
            if (decompressor == null)
                decompressor = new Decompressor();
            var frameLen = FrameLength(src, off, off + len);
            if (frameLen < 0)
                throw new InvalidDataException("zstd: not a frame");
            return decompressor.Unwrap(new ReadOnlySpan<byte>(src, off, frameLen), new Span<byte>(dst, dstOff, dstLen));
        }
    }
}
