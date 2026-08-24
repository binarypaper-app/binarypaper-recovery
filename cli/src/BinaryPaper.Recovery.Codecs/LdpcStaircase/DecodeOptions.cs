namespace LdpcStaircase
{
    /// <summary>
    /// Immutable knobs for <see cref="LdpcStaircaseCodec.Decode(byte[][], bool[], DecodeOptions)"/>.
    /// Start from <see cref="Default"/> and derive with the <c>With*</c> methods.
    ///
    /// <para>None of these change the byte-level contract of a decode that runs to completion;
    /// they bound or instrument it.</para>
    /// </summary>
    public sealed class DecodeOptions
    {
        private DecodeOptions(bool residualSolveEnabled, int maxResidualUnknowns,
            ProgressListener progressListener)
        {
            ResidualSolveEnabled = residualSolveEnabled;
            MaxResidualUnknowns = maxResidualUnknowns;
            ProgressListener = progressListener;
        }

        /// <summary>
        /// Residual solve enabled, no bound on the residual system, no progress listener.
        /// </summary>
        public static DecodeOptions Default { get; } =
            new DecodeOptions(true, int.MaxValue, null);

        /// <summary>Whether the stage-2 residual solve may run.</summary>
        public bool ResidualSolveEnabled { get; }

        /// <summary>The residual-system size above which the solve is refused.</summary>
        public int MaxResidualUnknowns { get; }

        /// <summary>The attached progress callback, or <c>null</c>.</summary>
        public ProgressListener ProgressListener { get; }

        /// <summary>
        /// Enables or disables the stage-2 residual solve (SPEC.md section 6.2). Disabling it
        /// leaves a peeling-only decoder: much cheaper, but it strands recoverable sessions
        /// whenever the sparse graph stalls, so it is not a production setting. It exists to
        /// isolate the two stages in tests and benchmarks.
        /// </summary>
        public DecodeOptions WithResidualSolve(bool enabled)
        {
            return new DecodeOptions(enabled, MaxResidualUnknowns, ProgressListener);
        }

        /// <summary>
        /// Refuses to start the residual solve when more than <paramref name="max"/> variables
        /// remain unknown after peeling, reporting the decode as failed instead
        /// (<see cref="DecodeResult.ResidualSolveSkipped"/>).
        ///
        /// <para>This is the seam through which a product enforces a memory seatbelt: the
        /// solve's working set grows as <c>|M| * |U| / 8</c> bytes of coefficients plus one
        /// symbol-sized right-hand side per unresolved row, and its time grows faster still.
        /// Callers that must not exceed a heap budget should set this rather than discover the
        /// limit as an <see cref="System.OutOfMemoryException"/>.</para>
        /// </summary>
        public DecodeOptions WithMaxResidualUnknowns(int max)
        {
            if (max < 0)
            {
                throw new LdpcStaircaseException("maxResidualUnknowns must be >= 0; got " + max + ".");
            }

            return new DecodeOptions(ResidualSolveEnabled, max, ProgressListener);
        }

        /// <summary>Attaches (or with <c>null</c>, clears) a decode progress callback.</summary>
        public DecodeOptions WithProgressListener(ProgressListener listener)
        {
            return new DecodeOptions(ResidualSolveEnabled, MaxResidualUnknowns, listener);
        }
    }
}
