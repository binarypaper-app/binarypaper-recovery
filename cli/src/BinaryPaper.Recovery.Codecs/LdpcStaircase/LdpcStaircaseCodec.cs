using System;

namespace LdpcStaircase
{
    /// <summary>
    /// Systematic, fixed-rate LDPC-Staircase erasure coder over GF(2), following the RFC 5170
    /// section 6 core constrained to the profile in SPEC.md. Given <c>K</c> equal-length source
    /// symbols it produces <c>R</c> repair symbols by sparse XOR; a decoder can usually
    /// reconstruct the source from <c>K + D</c> received symbols.
    ///
    /// <para>Unlike an MDS code such as Reed-Solomon there is <b>no "any <c>K</c> of <c>N</c>"
    /// guarantee</b>. <c>D</c> is a margin that depends on the graph, the seed, and the loss
    /// pattern, and must be measured for the parameters in use. Always read completion from
    /// <see cref="DecodeResult.IsComplete"/>.</para>
    ///
    /// <para>The coder is deliberately abstract: symbol byte arrays in, symbol byte arrays out.
    /// It performs no I/O, compression, encryption, or page layout. Symbols may be any positive
    /// length; the code operates byte-wise over GF(2).</para>
    ///
    /// <para>Instances are immutable and safe to share between threads; <see cref="Encode"/>
    /// and <see cref="Decode(byte[][], bool[])"/> operate only on the caller's arrays.</para>
    ///
    /// <para>The type is named <c>LdpcStaircaseCodec</c> rather than <c>LdpcStaircase</c>
    /// because the namespace already holds that name; the Java implementation's class is
    /// <c>LdpcStaircase</c>.</para>
    /// </summary>
    public sealed class LdpcStaircaseCodec
    {
        /// <summary>The default profile value for <c>N1</c>.</summary>
        public const int DefaultLeftDegree = ParityCheckMatrix.DefaultLeftDegree;

        private static readonly int[] NoIndices = new int[0];

        private readonly ParityCheckMatrix _matrix;
        private readonly int _sourceSymbolCount;
        private readonly int _repairSymbolCount;
        private readonly int _encodingSymbolCount;

        private LdpcStaircaseCodec(ParityCheckMatrix matrix)
        {
            _matrix = matrix;
            _sourceSymbolCount = matrix.SourceSymbolCount;
            _repairSymbolCount = matrix.RepairSymbolCount;
            _encodingSymbolCount = matrix.EncodingSymbolCount;
        }

        /// <summary>Number of source symbols, <c>K</c>.</summary>
        public int SourceSymbolCount => _sourceSymbolCount;

        /// <summary>Number of repair symbols, <c>R</c>.</summary>
        public int RepairSymbolCount => _repairSymbolCount;

        /// <summary>Number of encoding symbols, <c>N = K + R</c>.</summary>
        public int EncodingSymbolCount => _encodingSymbolCount;

        /// <summary>The configured number of ones per source column, <c>N1</c>.</summary>
        public int LeftDegree => _matrix.LeftDegree;

        /// <summary>The PRNG seed this code's matrix was built from.</summary>
        public int Seed => _matrix.Seed;

        /// <summary>The parity check matrix, exposed for analysis and loss simulation.</summary>
        public ParityCheckMatrix ParityCheckMatrix => _matrix;

        /// <summary>
        /// Creates a coder for <c>K</c> source and <c>R</c> repair symbols, building the parity
        /// check matrix from <paramref name="seed"/>. Requires <c>K &gt;= 1</c>,
        /// <c>R &gt;= 0</c>, <c>1 &lt;= N1 &lt;= R</c> when <c>R &gt;= 1</c>, and
        /// <paramref name="seed"/> in <c>[1, 2147483646]</c>.
        /// </summary>
        public static LdpcStaircaseCodec Create(int sourceSymbolCount, int repairSymbolCount,
            int seed, int leftDegree = DefaultLeftDegree)
        {
            return new LdpcStaircaseCodec(
                ParityCheckMatrix.Build(sourceSymbolCount, repairSymbolCount, leftDegree, seed));
        }

        /// <summary>
        /// Wraps an already-built matrix. Matrix construction is the only per-code setup cost,
        /// so a caller that encodes and later decodes the same code can build it once.
        /// </summary>
        public static LdpcStaircaseCodec Of(ParityCheckMatrix matrix)
        {
            if (matrix == null)
            {
                throw new LdpcStaircaseException("matrix must not be null.");
            }

            return new LdpcStaircaseCodec(matrix);
        }

        /// <summary>
        /// Encodes in place. <paramref name="symbols"/> must have <c>K + R</c> entries; slots
        /// <c>[0..K)</c> hold the caller-supplied source symbols, of equal positive length, and
        /// slots <c>[K..K+R)</c> are overwritten with the computed repair symbols (allocated if
        /// null or of the wrong length). With <c>R == 0</c> this only validates the source
        /// symbols.
        ///
        /// <para>Repair symbols are produced in ascending order because each one is the running
        /// XOR of its equation's source symbols with its predecessor (SPEC.md section 5).</para>
        /// </summary>
        public void Encode(byte[][] symbols)
        {
            int symbolLength = ValidateSourceSymbols(symbols);

            byte[] previous = null;
            for (int p = 0; p < _repairSymbolCount; p++)
            {
                byte[] repair = EnsureSymbol(symbols, _sourceSymbolCount + p, symbolLength);
                Array.Clear(repair, 0, repair.Length);

                int[] columns = _matrix.RowColumnsInternal[p];
                for (int idx = 0; idx < columns.Length; idx++)
                {
                    int c = columns[idx];
                    if (c < _sourceSymbolCount)
                    {
                        Symbols.XorInto(repair, symbols[c]);
                    }
                }

                if (previous != null)
                {
                    Symbols.XorInto(repair, previous);
                }

                previous = repair;
            }
        }

        /// <summary>
        /// Returns <c>true</c> if all <c>K + R</c> symbols satisfy every parity equation — i.e.
        /// each row of the parity check matrix XORs its symbols to zero. Expects a fully
        /// populated symbol array of equal positive lengths.
        /// </summary>
        public bool Verify(byte[][] symbols)
        {
            int symbolLength = ValidateAllSymbols(symbols);

            var accumulator = new byte[symbolLength];
            for (int i = 0; i < _repairSymbolCount; i++)
            {
                Array.Clear(accumulator, 0, accumulator.Length);
                int[] columns = _matrix.RowColumnsInternal[i];
                for (int idx = 0; idx < columns.Length; idx++)
                {
                    Symbols.XorInto(accumulator, symbols[columns[idx]]);
                }

                if (!Symbols.IsZero(accumulator))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Decodes with <see cref="DecodeOptions.Default"/>. See
        /// <see cref="Decode(byte[][], bool[], DecodeOptions)"/>.
        /// </summary>
        public DecodeResult Decode(byte[][] symbols, bool[] symbolPresent)
        {
            return Decode(symbols, symbolPresent, DecodeOptions.Default);
        }

        /// <summary>
        /// Recovers missing symbols in place. <paramref name="symbolPresent"/> marks which of
        /// the <c>K + R</c> symbols currently hold valid bytes; present symbols are read for
        /// their length, and recovered symbol slots are (re)allocated and filled.
        ///
        /// <para>Both arrays are <b>mutated</b>: every symbol the decode recovers is written
        /// back and marked present. Missing repair symbols fall out of the same equations at no
        /// extra cost and are recovered too.</para>
        ///
        /// <para>Decoding runs degree-one peeling to a fixed point and, only if source symbols
        /// remain unknown, one complete residual GF(2) solve (SPEC.md section 6). It never
        /// iterates between the two.</para>
        ///
        /// <para>A received set that does not determine the source is <b>not</b> an error: the
        /// returned <see cref="DecodeResult"/> reports <see cref="DecodeResult.IsComplete"/>
        /// false and lists what is still missing. Exceptions signal malformed input only.</para>
        /// </summary>
        public DecodeResult Decode(byte[][] symbols, bool[] symbolPresent, DecodeOptions options)
        {
            if (options == null)
            {
                throw new LdpcStaircaseException("decode: options must not be null.");
            }

            int symbolLength = ValidatePresentSymbols(symbols, symbolPresent);
            int k = _sourceSymbolCount;
            int n = _encodingSymbolCount;

            int missingAtEntry = 0;
            for (int c = 0; c < n; c++)
            {
                if (!symbolPresent[c])
                {
                    missingAtEntry++;
                }
            }

            ProgressListener listener = options.ProgressListener;
            if (missingAtEntry == 0)
            {
                listener?.Invoke(1, 1);
                return new DecodeResult(true, DecodeStage.None, 0, 0, NoIndices, false, 0, 0, -1);
            }

            // Work model for the progress fraction: the peeling pass costs about one visit per
            // matrix entry, and room is reserved up front for a residual solve because whether
            // one is needed is only known after peeling stalls.
            long peelCost = _matrix.EntryCount + 1L;
            long residualCost = options.ResidualSolveEnabled ? (long)missingAtEntry * missingAtEntry : 0L;
            Progress progress = listener == null ? null : new Progress(listener, peelCost + residualCost);

            var state = new DecodeState(this, symbols, symbolPresent, symbolLength);
            Peel(state, progress, peelCost);

            int missingSource = CountMissingSource(symbolPresent, k);
            if (missingSource == 0)
            {
                progress?.Done();
                return new DecodeResult(true, DecodeStage.Peeling, state.RecoveredSource,
                    state.RecoveredTotal, NoIndices, false, 0, 0, -1,
                    state.PeelPasses, state.ResidualSolvePasses);
            }

            // Peeling stalled. Everything still unknown is a variable of the residual system:
            // after the fixed point no row has exactly one unknown, and rows with none say
            // nothing about the rest.
            int residualUnknowns = 0;
            for (int c = 0; c < n; c++)
            {
                if (!symbolPresent[c])
                {
                    residualUnknowns++;
                }
            }

            int residualRows = 0;
            for (int i = 0; i < _repairSymbolCount; i++)
            {
                if (state.Unknown[i] >= 2)
                {
                    residualRows++;
                }
            }

            if (!options.ResidualSolveEnabled || residualUnknowns > options.MaxResidualUnknowns)
            {
                progress?.Done();
                return new DecodeResult(false, DecodeStage.Peeling, state.RecoveredSource,
                    state.RecoveredTotal, MissingSourceIndices(symbolPresent, k, missingSource), true,
                    residualUnknowns, residualRows, -1,
                    state.PeelPasses, state.ResidualSolvePasses);
            }

            int rank = ResidualSolve(state, residualUnknowns, residualRows, progress, peelCost,
                residualCost);

            missingSource = CountMissingSource(symbolPresent, k);
            progress?.Done();

            return new DecodeResult(missingSource == 0, DecodeStage.ResidualSolve,
                state.RecoveredSource, state.RecoveredTotal,
                MissingSourceIndices(symbolPresent, k, missingSource), false, residualUnknowns,
                residualRows, rank, state.PeelPasses, state.ResidualSolvePasses);
        }

        /// <summary>
        /// Splits <paramref name="data"/> into <paramref name="sourceSymbolCount"/> zero-padded
        /// symbols of <paramref name="symbolSize"/> bytes each. <paramref name="symbolSize"/>
        /// must be positive and <c>data.Length &lt;= sourceSymbolCount * symbolSize</c>.
        /// </summary>
        public static byte[][] Split(byte[] data, int sourceSymbolCount, int symbolSize)
        {
            if (data == null)
            {
                throw new LdpcStaircaseException("split: data must not be null.");
            }

            if (sourceSymbolCount < 1)
            {
                throw new LdpcStaircaseException(
                    "split: sourceSymbolCount (K) must be >= 1; got " + sourceSymbolCount + ".");
            }

            if (symbolSize <= 0)
            {
                throw new LdpcStaircaseException("split: symbolSize must be > 0; got " + symbolSize + ".");
            }

            long capacity = (long)sourceSymbolCount * symbolSize;
            if (data.Length > capacity)
            {
                throw new LdpcStaircaseException(
                    "split: data length " + data.Length + " exceeds capacity " + capacity
                    + " (sourceSymbolCount * symbolSize).");
            }

            var symbols = new byte[sourceSymbolCount][];
            for (int i = 0; i < sourceSymbolCount; i++)
            {
                var symbol = new byte[symbolSize];
                int offset = i * symbolSize;
                int remaining = data.Length - offset;
                if (remaining > 0)
                {
                    Array.Copy(data, offset, symbol, 0, Math.Min(remaining, symbolSize));
                }

                symbols[i] = symbol;
            }

            return symbols;
        }

        /// <summary>
        /// Concatenates <paramref name="sourceSymbols"/> in order and returns the first
        /// <paramref name="outputLength"/> bytes.
        /// </summary>
        public static byte[] Join(byte[][] sourceSymbols, int outputLength)
        {
            if (sourceSymbols == null)
            {
                throw new LdpcStaircaseException("join: sourceSymbols must not be null.");
            }

            if (outputLength < 0)
            {
                throw new LdpcStaircaseException("join: outputLength must be >= 0; got " + outputLength + ".");
            }

            long total = 0;
            for (int i = 0; i < sourceSymbols.Length; i++)
            {
                if (sourceSymbols[i] == null)
                {
                    throw new LdpcStaircaseException("join: sourceSymbols[" + i + "] must not be null.");
                }

                total += sourceSymbols[i].Length;
            }

            if (outputLength > total)
            {
                throw new LdpcStaircaseException(
                    "join: outputLength " + outputLength + " exceeds concatenated length " + total + ".");
            }

            var output = new byte[outputLength];
            int written = 0;
            for (int i = 0; i < sourceSymbols.Length && written < outputLength; i++)
            {
                byte[] symbol = sourceSymbols[i];
                int copy = Math.Min(symbol.Length, outputLength - written);
                Array.Copy(symbol, 0, output, written, copy);
                written += copy;
            }

            return output;
        }

        // ------------------------------------------------------------------
        // Decoding
        // ------------------------------------------------------------------

        /// <summary>
        /// Per-decode mutable state: for every equation, how many of its variables are still
        /// unknown and the XOR of the ones that are known. When <c>Unknown[i]</c> falls to one,
        /// <c>Accumulator[i]</c> <em>is</em> the value of the remaining variable, so the
        /// accumulator buffer is handed straight over as the recovered symbol.
        /// </summary>
        private sealed class DecodeState
        {
            private readonly LdpcStaircaseCodec _codec;

            internal readonly byte[][] SymbolArray;
            internal readonly bool[] Present;
            internal readonly int[] Unknown;
            internal readonly byte[][] Accumulator;
            internal int RecoveredSource;
            internal int RecoveredTotal;

            // SPEC.md section 6 stage counters; see DecodeResult.PeelPassCount.
            internal int PeelPasses;
            internal int ResidualSolvePasses;

            internal DecodeState(LdpcStaircaseCodec codec, byte[][] symbols, bool[] present,
                int symbolLength)
            {
                _codec = codec;
                SymbolArray = symbols;
                Present = present;
                Unknown = new int[codec._repairSymbolCount];
                Accumulator = new byte[codec._repairSymbolCount][];

                for (int i = 0; i < codec._repairSymbolCount; i++)
                {
                    int[] columns = codec._matrix.RowColumnsInternal[i];
                    int missing = 0;
                    for (int idx = 0; idx < columns.Length; idx++)
                    {
                        if (!present[columns[idx]])
                        {
                            missing++;
                        }
                    }

                    Unknown[i] = missing;
                    if (missing == 0)
                    {
                        continue; // A fully known row can never contribute again.
                    }

                    var accumulated = new byte[symbolLength];
                    for (int idx = 0; idx < columns.Length; idx++)
                    {
                        int c = columns[idx];
                        if (present[c])
                        {
                            Symbols.XorInto(accumulated, symbols[c]);
                        }
                    }

                    Accumulator[i] = accumulated;
                }
            }

            internal void Recover(int variable, byte[] value)
            {
                SymbolArray[variable] = value;
                Present[variable] = true;
                RecoveredTotal++;
                if (variable < _codec._sourceSymbolCount)
                {
                    RecoveredSource++;
                }
            }
        }

        /// <summary>SPEC.md section 6.1 — degree-one peeling to a fixed point.</summary>
        private void Peel(DecodeState state, Progress progress, long peelCost)
        {
            state.PeelPasses++;
            int[][] rowColumns = _matrix.RowColumnsInternal;
            int[][] columnRows = _matrix.ColumnRowsInternal;
            int[] unknown = state.Unknown;
            byte[][] accumulator = state.Accumulator;

            // A row is pushed only on the transition to exactly one unknown, and `unknown`
            // never rises, so one slot per row is enough.
            var stack = new int[Math.Max(1, _repairSymbolCount)];
            int top = 0;
            for (int i = 0; i < _repairSymbolCount; i++)
            {
                if (unknown[i] == 1)
                {
                    stack[top++] = i;
                }
            }

            long work = 0;
            while (top > 0)
            {
                int row = stack[--top];
                if (unknown[row] != 1)
                {
                    continue; // Another equation already resolved this row's last unknown.
                }

                int[] columns = rowColumns[row];
                int variable = -1;
                for (int idx = 0; idx < columns.Length; idx++)
                {
                    if (!state.Present[columns[idx]])
                    {
                        variable = columns[idx];
                        break;
                    }
                }

                byte[] value = accumulator[row];
                accumulator[row] = null;
                unknown[row] = 0;
                if (variable < 0)
                {
                    continue; // Defensive: the count and the present-mask disagreed.
                }

                state.Recover(variable, value);

                int[] rows = columnRows[variable];
                for (int idx = 0; idx < rows.Length; idx++)
                {
                    int other = rows[idx];
                    if (other == row)
                    {
                        continue;
                    }

                    int remaining = --unknown[other];
                    if (remaining == 0)
                    {
                        accumulator[other] = null; // Fully known; its accumulator is dead.
                        continue;
                    }

                    if (accumulator[other] != null)
                    {
                        Symbols.XorInto(accumulator[other], value);
                    }

                    if (remaining == 1)
                    {
                        if (top == stack.Length)
                        {
                            Array.Resize(ref stack, top * 2);
                        }

                        stack[top++] = other;
                    }
                }

                if (progress != null)
                {
                    work += columns.Length + rows.Length;
                    progress.Set(Math.Min(work, peelCost));
                }
            }
        }

        /// <summary>
        /// SPEC.md section 6.2 — one complete residual solve.
        ///
        /// <para>The rows that still hold two or more unknowns form a GF(2) system in those
        /// unknowns, with each row's accumulator as its right-hand side. Reducing it to reduced
        /// row echelon form — applying every row operation to the symbol right-hand sides as
        /// well — recovers exactly those variables the received set determines: a pivot variable
        /// is known when its RREF row mentions no other variable. Anything left is genuinely
        /// undetermined, which is why no second peeling pass could help.</para>
        /// </summary>
        /// <returns>The rank of the residual system.</returns>
        private int ResidualSolve(DecodeState state, int unknownCount, int rowCount,
            Progress progress, long peelCost, long residualCost)
        {
            state.ResidualSolvePasses++;
            int n = _encodingSymbolCount;

            // Map each still-unknown symbol index onto a column of the residual system.
            var columnOfSymbol = new int[n];
            for (int i = 0; i < n; i++)
            {
                columnOfSymbol[i] = -1;
            }

            var symbolOfColumn = new int[unknownCount];
            int next = 0;
            for (int c = 0; c < n; c++)
            {
                if (!state.Present[c])
                {
                    columnOfSymbol[c] = next;
                    symbolOfColumn[next] = c;
                    next++;
                }
            }

            int words = (unknownCount + 63) >> 6;
            var coefficients = new ulong[rowCount][];
            var rightHandSides = new byte[rowCount][];
            int filled = 0;
            for (int i = 0; i < _repairSymbolCount && filled < rowCount; i++)
            {
                if (state.Unknown[i] < 2)
                {
                    continue;
                }

                var bits = new ulong[words];
                int[] columns = _matrix.RowColumnsInternal[i];
                for (int idx = 0; idx < columns.Length; idx++)
                {
                    int column = columnOfSymbol[columns[idx]];
                    if (column >= 0)
                    {
                        bits[column >> 6] |= 1UL << (column & 63);
                    }
                }

                coefficients[filled] = bits;
                rightHandSides[filled] = state.Accumulator[i];
                filled++;
            }

            // Gauss-Jordan to reduced row echelon form.
            int rank = 0;
            var pivotColumn = new int[Math.Min(rowCount, unknownCount)];
            for (int column = 0; column < unknownCount && rank < rowCount; column++)
            {
                int word = column >> 6;
                ulong mask = 1UL << (column & 63);

                int pivot = -1;
                for (int row = rank; row < rowCount; row++)
                {
                    if ((coefficients[row][word] & mask) != 0)
                    {
                        pivot = row;
                        break;
                    }
                }

                if (pivot < 0)
                {
                    continue; // Free variable: no equation can still resolve it alone.
                }

                if (pivot != rank)
                {
                    ulong[] bits = coefficients[pivot];
                    coefficients[pivot] = coefficients[rank];
                    coefficients[rank] = bits;
                    byte[] rhs = rightHandSides[pivot];
                    rightHandSides[pivot] = rightHandSides[rank];
                    rightHandSides[rank] = rhs;
                }

                for (int row = 0; row < rowCount; row++)
                {
                    if (row == rank || (coefficients[row][word] & mask) == 0)
                    {
                        continue;
                    }

                    Symbols.XorInto(coefficients[row], coefficients[rank]);
                    Symbols.XorInto(rightHandSides[row], rightHandSides[rank]);
                }

                pivotColumn[rank] = column;
                rank++;

                if (progress != null)
                {
                    // Divide before multiplying: residualCost is already quadratic in the
                    // missing count, and the product would be the only overflow risk here.
                    progress.Set(peelCost + residualCost / unknownCount * (column + 1L));
                }
            }

            // A pivot row whose only remaining coefficient is its own pivot determines that
            // variable outright; one that still mentions a free variable does not.
            for (int row = 0; row < rank; row++)
            {
                ulong[] bits = coefficients[row];
                int ones = 0;
                for (int w = 0; w < words && ones < 2; w++)
                {
                    ones += Symbols.PopCount(bits[w]);
                }

                if (ones != 1)
                {
                    continue;
                }

                state.Recover(symbolOfColumn[pivotColumn[row]], rightHandSides[row]);
            }

            return rank;
        }

        private static int CountMissingSource(bool[] present, int sourceSymbolCount)
        {
            int missing = 0;
            for (int j = 0; j < sourceSymbolCount; j++)
            {
                if (!present[j])
                {
                    missing++;
                }
            }

            return missing;
        }

        private static int[] MissingSourceIndices(bool[] present, int sourceSymbolCount, int missing)
        {
            if (missing == 0)
            {
                return NoIndices;
            }

            var indices = new int[missing];
            int next = 0;
            for (int j = 0; j < sourceSymbolCount && next < missing; j++)
            {
                if (!present[j])
                {
                    indices[next++] = j;
                }
            }

            return indices;
        }

        /// <summary>
        /// Throttled progress relay: forwards at most ~200 <see cref="ProgressListener"/> calls
        /// over one decode, clamps to <c>[0, total]</c>, and guarantees one final exact-total
        /// tick via <see cref="Done"/>.
        /// </summary>
        private sealed class Progress
        {
            private readonly ProgressListener _listener;
            private readonly long _total;
            private readonly long _minStep;
            private long _lastEmitted = -1;

            internal Progress(ProgressListener listener, long total)
            {
                _listener = listener;
                _total = Math.Max(1, total);
                _minStep = Math.Max(1, _total / 200);
            }

            internal void Set(long completed)
            {
                long clamped = Math.Min(Math.Max(completed, 0), _total);
                if (clamped < _total && clamped - _lastEmitted >= _minStep)
                {
                    _lastEmitted = clamped;
                    _listener(clamped, _total);
                }
            }

            internal void Done()
            {
                _lastEmitted = _total;
                _listener(_total, _total);
            }
        }

        // ------------------------------------------------------------------
        // Validation
        // ------------------------------------------------------------------

        private static byte[] EnsureSymbol(byte[][] symbols, int index, int symbolLength)
        {
            byte[] symbol = symbols[index];
            if (symbol == null || symbol.Length != symbolLength)
            {
                symbol = new byte[symbolLength];
                symbols[index] = symbol;
            }

            return symbol;
        }

        /// <summary>Validates the <c>K</c> source slots of an encode array; returns their length.</summary>
        private int ValidateSourceSymbols(byte[][] symbols)
        {
            RequireArrayLength(symbols, "encode");

            int symbolLength = -1;
            for (int i = 0; i < _sourceSymbolCount; i++)
            {
                byte[] symbol = symbols[i];
                if (symbol == null)
                {
                    throw new LdpcStaircaseException("encode: source symbol " + i + " must not be null.");
                }

                if (symbolLength < 0)
                {
                    symbolLength = RequirePositiveLength(symbol.Length, "encode");
                }
                else if (symbol.Length != symbolLength)
                {
                    throw new LdpcStaircaseException(
                        "encode: all source symbols must have equal length; symbol " + i
                        + " has length " + symbol.Length + " but expected " + symbolLength + ".");
                }
            }

            return symbolLength;
        }

        /// <summary>Validates a fully populated symbol array; returns the common symbol length.</summary>
        private int ValidateAllSymbols(byte[][] symbols)
        {
            RequireArrayLength(symbols, "verify");

            int symbolLength = -1;
            for (int i = 0; i < _encodingSymbolCount; i++)
            {
                byte[] symbol = symbols[i];
                if (symbol == null)
                {
                    throw new LdpcStaircaseException("verify: symbol " + i + " must not be null.");
                }

                if (symbolLength < 0)
                {
                    symbolLength = RequirePositiveLength(symbol.Length, "verify");
                }
                else if (symbol.Length != symbolLength)
                {
                    throw new LdpcStaircaseException(
                        "verify: all symbols must have equal length; symbol " + i + " has length "
                        + symbol.Length + " but expected " + symbolLength + ".");
                }
            }

            return symbolLength;
        }

        /// <summary>Validates the present symbols of a decode array; returns their common length.</summary>
        private int ValidatePresentSymbols(byte[][] symbols, bool[] symbolPresent)
        {
            RequireArrayLength(symbols, "decode");

            if (symbolPresent == null)
            {
                throw new LdpcStaircaseException("decode: symbolPresent must not be null.");
            }

            if (symbolPresent.Length != _encodingSymbolCount)
            {
                throw new LdpcStaircaseException(
                    "decode: symbolPresent must have " + _encodingSymbolCount + " entries (K + R); got "
                    + symbolPresent.Length + ".");
            }

            int symbolLength = -1;
            for (int i = 0; i < _encodingSymbolCount; i++)
            {
                if (!symbolPresent[i])
                {
                    continue;
                }

                byte[] symbol = symbols[i];
                if (symbol == null)
                {
                    throw new LdpcStaircaseException(
                        "decode: symbol " + i + " is marked present but is null.");
                }

                if (symbolLength < 0)
                {
                    symbolLength = RequirePositiveLength(symbol.Length, "decode");
                }
                else if (symbol.Length != symbolLength)
                {
                    throw new LdpcStaircaseException(
                        "decode: present symbols have differing lengths (" + symbolLength + " vs "
                        + symbol.Length + ").");
                }
            }

            if (symbolLength < 0)
            {
                throw new LdpcStaircaseException("decode: at least one symbol must be present.");
            }

            return symbolLength;
        }

        private void RequireArrayLength(byte[][] symbols, string operation)
        {
            if (symbols == null)
            {
                throw new LdpcStaircaseException(operation + ": symbols must not be null.");
            }

            if (symbols.Length != _encodingSymbolCount)
            {
                throw new LdpcStaircaseException(operation + ": expected " + _encodingSymbolCount
                    + " symbols (K + R); got " + symbols.Length + ".");
            }
        }

        private static int RequirePositiveLength(int symbolLength, string operation)
        {
            if (symbolLength <= 0)
            {
                throw new LdpcStaircaseException(
                    operation + ": symbol length must be > 0; got " + symbolLength + ".");
            }

            return symbolLength;
        }

        public override string ToString()
        {
            return "LdpcStaircaseCodec{K=" + _sourceSymbolCount + ", R=" + _repairSymbolCount
                + ", N1=" + _matrix.LeftDegree + ", seed=" + _matrix.Seed + "}";
        }
    }
}
