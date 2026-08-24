namespace LdpcStaircase
{
    /// <summary>
    /// Byte-array helpers for the GF(2) symbol arithmetic. Addition, subtraction, and the
    /// whole code's arithmetic are bitwise XOR, so these three operations are all the codec
    /// needs on symbol payloads.
    /// </summary>
    internal static class Symbols
    {
        /// <summary>
        /// <c>dst ^= src</c>. Both arrays must have the same length.
        ///
        /// <para>This is the decoder's hottest loop — the residual solve XORs one whole symbol
        /// per eliminated row — so on runtimes that have <c>Span</c> it is widened to 64-bit
        /// words. That is safe rather than merely fast to write: XOR is byte-order agnostic, so
        /// the reinterpretation applies identically to both operands and the result, and the
        /// bytes written are the same on every platform and on both target frameworks. The
        /// <c>netstandard2.0</c> path stays a plain unrolled byte loop because a wide view there
        /// would need either a <c>System.Memory</c> package reference or <c>unsafe</c> code, and
        /// this repository deliberately has neither.</para>
        /// </summary>
        internal static void XorInto(byte[] dst, byte[] src)
        {
#if NETSTANDARD2_0
            int length = dst.Length;
            int i = 0;
            int limit = length - 7;
            for (; i < limit; i += 8)
            {
                dst[i] ^= src[i];
                dst[i + 1] ^= src[i + 1];
                dst[i + 2] ^= src[i + 2];
                dst[i + 3] ^= src[i + 3];
                dst[i + 4] ^= src[i + 4];
                dst[i + 5] ^= src[i + 5];
                dst[i + 6] ^= src[i + 6];
                dst[i + 7] ^= src[i + 7];
            }

            for (; i < length; i++)
            {
                dst[i] ^= src[i];
            }
#else
            // The (Span<byte>) casts are load-bearing: byte[] binds to the ReadOnlySpan
            // overload of Cast by default, which cannot be written through.
            System.Span<ulong> wideDst =
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>((System.Span<byte>)dst);
            System.Span<ulong> wideSrc =
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>((System.Span<byte>)src);
            for (int w = 0; w < wideDst.Length; w++)
            {
                wideDst[w] ^= wideSrc[w];
            }

            for (int i = wideDst.Length * 8; i < dst.Length; i++)
            {
                dst[i] ^= src[i];
            }
#endif
        }

        /// <summary><c>a ^= b</c> over a bitset word array. Both must have the same length.</summary>
        internal static void XorInto(ulong[] a, ulong[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                a[i] ^= b[i];
            }
        }

        /// <summary>True when every byte is zero — the parity-check condition for one equation.</summary>
        internal static bool IsZero(byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Population count of a 64-bit word. Written out because
        /// <c>System.Numerics.BitOperations</c> does not exist on <c>netstandard2.0</c>.
        /// </summary>
        internal static int PopCount(ulong value)
        {
            value -= (value >> 1) & 0x5555555555555555UL;
            value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
            value = (value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((value * 0x0101010101010101UL) >> 56);
        }
    }
}
