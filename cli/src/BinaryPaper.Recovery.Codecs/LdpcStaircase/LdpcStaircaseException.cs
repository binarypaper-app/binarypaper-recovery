using System;

namespace LdpcStaircase
{
    /// <summary>
    /// Thrown for invalid parameters or malformed input to the <see cref="LdpcStaircaseCodec"/>:
    /// bad <c>K</c>/<c>R</c>/<c>N1</c>/<c>seed</c> values, mismatched or zero-length symbols,
    /// or a structurally invalid symbol array.
    ///
    /// <para>A <em>failed decode</em> is not an exception. LDPC-Staircase recovery is
    /// probabilistic (SPEC.md section 6.3), so a received set that does not determine the
    /// source returns a <see cref="DecodeResult"/> with <see cref="DecodeResult.IsComplete"/>
    /// false rather than throwing.</para>
    /// </summary>
    public sealed class LdpcStaircaseException : Exception
    {
        public LdpcStaircaseException(string message) : base(message)
        {
        }

        public LdpcStaircaseException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
