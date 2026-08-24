using System.Text;

namespace LdpcStaircase
{
    /// <summary>How far through SPEC.md section 6 a decode had to go.</summary>
    public enum DecodeStage
    {
        /// <summary>Nothing was missing; the decode did no work.</summary>
        None,

        /// <summary>Degree-one peeling was the last stage that ran (section 6.1).</summary>
        Peeling,

        /// <summary>The residual GF(2) solve was attempted (section 6.2).</summary>
        ResidualSolve
    }

    /// <summary>
    /// The outcome of one <see cref="LdpcStaircaseCodec.Decode(byte[][], bool[], DecodeOptions)"/> call.
    ///
    /// <para>LDPC-Staircase recovery is probabilistic, not MDS: there is no "any <c>K</c> of
    /// <c>N</c>" guarantee, and a session holding <c>K</c> or more symbols can still fail.
    /// Callers MUST decide completion from <see cref="IsComplete"/> and never from a
    /// received-symbol count (SPEC.md section 6.3).</para>
    ///
    /// <para>The remaining members describe how much work the decode needed, which is the data
    /// a product uses to measure its recovery margin <c>D</c> and to size a memory
    /// seatbelt.</para>
    /// </summary>
    public sealed class DecodeResult
    {
        private readonly int[] _missingSourceIndices;

        internal DecodeResult(bool isComplete, DecodeStage stage, int recoveredSourceCount,
            int recoveredSymbolCount, int[] missingSourceIndices, bool residualSolveSkipped,
            int residualUnknownCount, int residualRowCount, int residualRank,
            int peelPassCount = 0, int residualSolvePassCount = 0)
        {
            IsComplete = isComplete;
            Stage = stage;
            RecoveredSourceCount = recoveredSourceCount;
            RecoveredSymbolCount = recoveredSymbolCount;
            _missingSourceIndices = missingSourceIndices;
            ResidualSolveSkipped = residualSolveSkipped;
            ResidualUnknownCount = residualUnknownCount;
            ResidualRowCount = residualRowCount;
            ResidualRank = residualRank;
            PeelPassCount = peelPassCount;
            ResidualSolvePassCount = residualSolvePassCount;
        }

        /// <summary>True when all <c>K</c> source symbols are present after the decode.</summary>
        public bool IsComplete { get; }

        /// <summary>The furthest stage this decode reached.</summary>
        public DecodeStage Stage { get; }

        /// <summary>How many missing <em>source</em> symbols this call recovered.</summary>
        public int RecoveredSourceCount { get; }

        /// <summary>
        /// How many symbols in total this call recovered. Missing repair symbols fall out of
        /// the same equations at no extra cost, so they are recovered and written back too.
        /// </summary>
        public int RecoveredSymbolCount { get; }

        /// <summary>
        /// True when peeling stalled but the residual solve did not run — either it was
        /// disabled, or the residual system exceeded
        /// <see cref="DecodeOptions.MaxResidualUnknowns"/>.
        /// </summary>
        public bool ResidualSolveSkipped { get; }

        /// <summary>
        /// The number of variables still unknown when peeling reached its fixed point, i.e.
        /// the width of the residual system. Zero when peeling succeeded.
        /// </summary>
        public int ResidualUnknownCount { get; }

        /// <summary>The number of unresolved equations in the residual system.</summary>
        public int ResidualRowCount { get; }

        /// <summary>
        /// The GF(2) rank of the residual system, or <c>-1</c> if no solve ran. A rank below
        /// <see cref="ResidualUnknownCount"/> means the received set left free variables and
        /// could not determine every symbol.
        /// </summary>
        public int ResidualRank { get; }

        /// <summary>
        /// How many times degree-one peeling (SPEC.md section 6.1) ran during this decode.
        /// <para>
        /// SPEC.md section 6 requires the decoder to peel to a fixed point and then stop, so
        /// this is <c>0</c> when nothing was missing and <c>1</c> otherwise — never more. The
        /// count is reported rather than merely asserted internally because the profile's
        /// two-stage, non-iterating shape is a deliberate and externally meaningful property;
        /// see <c>ProfileInvariantsTests</c>, which pins it across a spread of loss patterns.
        /// </para>
        /// </summary>
        public int PeelPassCount { get; }

        /// <summary>
        /// How many times the residual GF(2) solve (SPEC.md section 6.2) ran during this decode.
        /// Never more than <c>1</c>: the profile permits at most one solve of the complete
        /// residual system, and forbids returning to peeling afterwards.
        /// </summary>
        public int ResidualSolvePassCount { get; }

        /// <summary>True when peeling stalled and the residual solve was attempted.</summary>
        public bool ResidualSolveAttempted => Stage == DecodeStage.ResidualSolve;

        /// <summary>The source symbol indices still missing, ascending. Empty when complete.</summary>
        public int[] MissingSourceIndices() => (int[])_missingSourceIndices.Clone();

        public override string ToString()
        {
            var sb = new StringBuilder("DecodeResult{complete=").Append(IsComplete)
                .Append(", stage=").Append(Stage)
                .Append(", recoveredSource=").Append(RecoveredSourceCount)
                .Append(", recoveredSymbols=").Append(RecoveredSymbolCount)
                .Append(", missingSource=").Append(_missingSourceIndices.Length);
            if (ResidualUnknownCount > 0)
            {
                sb.Append(", residual=").Append(ResidualRowCount).Append('x').Append(ResidualUnknownCount)
                    .Append(", rank=").Append(ResidualRank)
                    .Append(", skipped=").Append(ResidualSolveSkipped);
            }

            return sb.Append('}').ToString();
        }
    }
}
