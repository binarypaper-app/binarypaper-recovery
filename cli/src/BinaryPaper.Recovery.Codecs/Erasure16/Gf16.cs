using System;

namespace Erasure16
{
    /// <summary>
    /// Arithmetic over the Galois field GF(2^16).
    ///
    /// Elements are integers in <c>0..65535</c>; bit <c>i</c> is the coefficient of
    /// <c>x^i</c> (so element <c>2</c> == <c>x</c>). Addition and subtraction are bitwise
    /// XOR. Multiplication is polynomial multiplication modulo the primitive reduction
    /// polynomial <c>0x1100B</c> (x^16 + x^12 + x^3 + x + 1), implemented with exp/log
    /// tables built from the generator alpha = 2. See SPEC.md section 1.
    /// </summary>
    public static class Gf16
    {
        /// <summary>Number of field elements: 2^16.</summary>
        public const int FieldSize = 65536;

        /// <summary>Multiplicative group order: 2^16 - 1. The exp table has this period.</summary>
        public const int Order = 65535;

        /// <summary>
        /// Primitive reduction polynomial x^16 + x^12 + x^3 + x + 1, as a 17-bit value.
        /// </summary>
        public const int ReductionPolynomial = 0x1100B;

        // expTable[i] = alpha^i for i in 0..65534 (alpha = 2). Length 65535.
        private static readonly ushort[] ExpTable;

        // logTable[expTable[i]] = i. logTable[0] is an undefined sentinel (0) that is
        // never read. Length 65536.
        private static readonly ushort[] LogTable;

        static Gf16()
        {
            ExpTable = new ushort[Order];
            LogTable = new ushort[FieldSize];

            // Build tables exactly per SPEC.md section 1:
            //   x = 1
            //   for i in 0..65534: exp[i] = x; log[x] = i; x <<= 1; if (x & 0x10000) x ^= 0x1100B
            int x = 1;
            for (int i = 0; i < Order; i++)
            {
                ExpTable[i] = (ushort)x;
                LogTable[x] = (ushort)i;

                x <<= 1;
                if ((x & 0x10000) != 0)
                {
                    x ^= ReductionPolynomial; // 17-bit reduction; result is 16-bit
                }
            }

            LogTable[0] = 0; // undefined sentinel; never read

            AssertFullPeriod();
        }

        /// <summary>
        /// Verifies that 0x1100B is primitive: every value in 1..65535 appears exactly
        /// once in the exp table (full period 65535) and the field closes back on 1.
        /// Throws <see cref="Erasure16Exception"/> if the field is malformed.
        /// </summary>
        private static void AssertFullPeriod()
        {
            var seen = new bool[FieldSize];
            for (int i = 0; i < Order; i++)
            {
                int v = ExpTable[i];
                if (v == 0)
                {
                    throw new Erasure16Exception(
                        "GF(2^16) table build failed: exp[" + i + "] == 0 (zero is not in the multiplicative group).");
                }

                if (seen[v])
                {
                    throw new Erasure16Exception(
                        "GF(2^16) reduction polynomial 0x1100B is not primitive: value " + v +
                        " repeats in the exp table before period 65535.");
                }

                seen[v] = true;
            }

            // All of 1..65535 must have been produced exactly once.
            for (int v = 1; v < FieldSize; v++)
            {
                if (!seen[v])
                {
                    throw new Erasure16Exception(
                        "GF(2^16) exp table does not cover the full multiplicative group: value " + v + " is missing.");
                }
            }

            // The group must wrap: alpha^Order == 1, i.e. exp[0] == 1.
            if (ExpTable[0] != 1)
            {
                throw new Erasure16Exception("GF(2^16) table build failed: exp[0] != 1.");
            }
        }

        /// <summary>
        /// Field multiplication. Returns 0 if either operand is 0, otherwise
        /// <c>exp[(log[a] + log[b]) mod 65535]</c>.
        /// </summary>
        public static ushort Mul(ushort a, ushort b)
        {
            if (a == 0 || b == 0)
            {
                return 0;
            }

            int sum = LogTable[a] + LogTable[b];
            if (sum >= Order)
            {
                sum -= Order; // sum is in 0..2*(Order-1); a single subtraction suffices for mod.
            }

            return ExpTable[sum];
        }

        /// <summary>
        /// Multiplicative inverse: <c>exp[(65535 - log[a]) mod 65535]</c> for <c>a != 0</c>.
        /// </summary>
        /// <exception cref="Erasure16Exception">If <paramref name="a"/> is 0.</exception>
        public static ushort Inv(ushort a)
        {
            if (a == 0)
            {
                throw new Erasure16Exception("GF(2^16): zero has no multiplicative inverse.");
            }

            int e = (Order - LogTable[a]) % Order;
            return ExpTable[e];
        }

        /// <summary>
        /// Field division <c>a / b == mul(a, inv(b))</c>.
        /// </summary>
        /// <exception cref="Erasure16Exception">If <paramref name="b"/> is 0.</exception>
        public static ushort Div(ushort a, ushort b)
        {
            if (b == 0)
            {
                throw new Erasure16Exception("GF(2^16): division by zero.");
            }

            if (a == 0)
            {
                return 0;
            }

            int diff = LogTable[a] - LogTable[b];
            if (diff < 0)
            {
                diff += Order;
            }

            return ExpTable[diff];
        }

        /// <summary>
        /// Returns <c>alpha^e</c> (the exp table lookup). The exponent is reduced
        /// modulo the group order 65535.
        /// </summary>
        public static ushort Exp(int e)
        {
            e %= Order;
            if (e < 0)
            {
                e += Order;
            }

            return ExpTable[e];
        }

        /// <summary>
        /// Returns the discrete logarithm of <paramref name="a"/> base alpha
        /// (the log table lookup). <c>log[0]</c> is an undefined sentinel.
        /// </summary>
        /// <exception cref="Erasure16Exception">If <paramref name="a"/> is 0.</exception>
        public static int Log(ushort a)
        {
            if (a == 0)
            {
                throw new Erasure16Exception("GF(2^16): logarithm of zero is undefined.");
            }

            return LogTable[a];
        }
    }
}
