using System;

namespace Erasure16
{
    /// <summary>
    /// Thrown for invalid parameters or malformed input to the <see cref="ReedSolomon16"/>
    /// erasure coder: bad <c>K</c>/<c>R</c> values, mismatched or odd shard lengths,
    /// or fewer than <c>K</c> present shards during reconstruction.
    /// </summary>
    public sealed class Erasure16Exception : Exception
    {
        public Erasure16Exception(string message) : base(message)
        {
        }

        public Erasure16Exception(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
