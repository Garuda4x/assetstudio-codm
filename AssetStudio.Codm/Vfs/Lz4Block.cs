using System;
using System.IO;

namespace AssetStudio.Codm.Vfs
{
    /// <summary>
    /// Raw LZ4 block decoder that stops when the output buffer is full, so the compressed length of the
    /// last block of a Qts entry (which the index does not store) need not be known.
    /// </summary>
    internal static class Lz4Block
    {
        /// <summary>Decode one block. Returns the number of source bytes consumed.</summary>
        public static int Decode(byte[] src, int srcOff, int srcEnd, byte[] dst, int dstOff, int dstEnd)
        {
            int ip = srcOff, op = dstOff;
            while (ip < srcEnd && op < dstEnd)
            {
                int token = src[ip++];
                int lit = token >> 4;
                if (lit == 15)
                {
                    int b;
                    do { b = src[ip++]; lit += b; } while (b == 255 && ip < srcEnd);
                }
                if (lit > 0)
                {
                    if (lit > srcEnd - ip) lit = srcEnd - ip;
                    if (lit > dstEnd - op) lit = dstEnd - op;
                    Buffer.BlockCopy(src, ip, dst, op, lit);
                    ip += lit;
                    op += lit;
                }
                if (op >= dstEnd || ip + 2 > srcEnd)
                    break;
                int offset = src[ip] | (src[ip + 1] << 8);
                ip += 2;
                if (offset == 0 || offset > op - dstOff)
                    throw new InvalidDataException($"LZ4: bad match offset {offset} at output {op - dstOff}");
                int ml = token & 15;
                if (ml == 15)
                {
                    int b;
                    do { b = src[ip++]; ml += b; } while (b == 255 && ip < srcEnd);
                }
                ml += 4;
                if (ml > dstEnd - op) ml = dstEnd - op;
                int mp = op - offset;
                for (int k = 0; k < ml; k++)
                    dst[op++] = dst[mp++];
            }
            return ip - srcOff;
        }
    }
}
