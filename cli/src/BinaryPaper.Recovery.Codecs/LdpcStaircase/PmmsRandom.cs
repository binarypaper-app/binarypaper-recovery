namespace LdpcStaircase
{
    /// <summary>
    /// The Park-Miller "minimal standard" pseudo-random number generator required by
    /// RFC 5170 section 5.7 and pinned by SPEC.md section 3. It is the only source of
    /// randomness in the codec: the parity check matrix is a pure function of
    /// <c>(K, R, N1, seed)</c> because every draw comes from one stream started at
    /// <c>seed</c>.
    ///
    /// <para>The recurrence is <c>state &lt;- 16807 * state mod (2^31 - 1)</c>, evaluated with
    /// exact 64-bit intermediates. RFC 5170's validation criterion — seed <c>1</c>, the
    /// 10,000th raw value is <c>1043618065</c> — is asserted by <c>PmmsRandomTests</c>.</para>
    ///
    /// <para>This class is not thread-safe; it is a mutable stream owned by one matrix build.</para>
    /// </summary>
    public sealed class PmmsRandom
    {
        /// <summary>The multiplier <c>A = 7^5</c>.</summary>
        public const int Multiplier = 16807;

        /// <summary>The modulus <c>M = 2^31 - 1</c> (a Mersenne prime).</summary>
        public const int Modulus = 2147483647;

        /// <summary>Smallest legal seed.</summary>
        public const int MinSeed = 1;

        /// <summary>Largest legal seed, <c>2^31 - 2</c>.</summary>
        public const int MaxSeed = 2147483646;

        private int _state;

        /// <summary>
        /// Creates a generator seeded with <paramref name="seed"/>, which must be in
        /// <c>[1, 2147483646]</c>. Seed <c>0</c> is excluded because it is the fixed point
        /// of the recurrence.
        /// </summary>
        public PmmsRandom(int seed)
        {
            if (seed < MinSeed || seed > MaxSeed)
            {
                throw new LdpcStaircaseException(
                    "seed must be in [" + MinSeed + ", " + MaxSeed + "]; got " + seed + ".");
            }

            _state = seed;
        }

        /// <summary>The current raw state, without advancing the stream.</summary>
        public int State => _state;

        /// <summary>
        /// Advances the stream and returns the new raw state, in <c>[1, 2147483646]</c>.
        /// </summary>
        public int Next()
        {
            // 16807 * state needs 47 bits, so the product is formed in long arithmetic
            // and reduced before it is narrowed back to int.
            _state = (int)((Multiplier * (long)_state) % Modulus);
            return _state;
        }

        /// <summary>
        /// Advances the stream and scales the new raw state into <c>[0, maxv)</c>, using
        /// the IEEE 754 binary64 formula RFC 5170 specifies (SPEC.md section 3):
        /// <c>floor(maxv * Next() / 2147483647.0)</c>.
        ///
        /// <para>The multiply-then-divide order is normative. <c>maxv * state</c> can reach
        /// 2^62 and is rounded to a 53-bit mantissa, so this is <em>not</em> interchangeable
        /// with exact 64-bit integer arithmetic; both operations are correctly rounded on
        /// every conforming runtime, which is what makes the result identical on .NET and
        /// the JVM. The <c>prngBounded</c> and <c>matrix</c> conformance vectors are the
        /// standing proof that the two agree.</para>
        /// </summary>
        public int NextBounded(int maxv)
        {
            if (maxv < 1)
            {
                throw new LdpcStaircaseException("NextBounded: maxv must be >= 1; got " + maxv + ".");
            }

            return (int)((double)maxv * (double)Next() / 2147483647.0);
        }
    }
}
