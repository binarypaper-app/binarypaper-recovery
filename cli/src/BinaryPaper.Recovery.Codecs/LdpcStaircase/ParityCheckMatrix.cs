using System;
using System.Collections.Generic;

namespace LdpcStaircase
{
    /// <summary>
    /// The sparse binary parity check matrix <c>H</c> of SPEC.md section 4: <c>R</c> rows
    /// (one per parity equation) by <c>N = K + R</c> columns (one per encoding symbol).
    /// Columns <c>0..K-1</c> are the source symbols; columns <c>K..N-1</c> are the repair
    /// symbols, carrying the staircase.
    ///
    /// <para>Each row is one equation constraining its symbols to XOR to zero. The matrix is
    /// a pure function of <c>(K, R, N1, seed)</c>: it is built by three ordered phases that
    /// share a single <see cref="PmmsRandom"/> stream, so two implementations given the same
    /// four values build the same graph. That order is normative — it fixes the sequence of
    /// PRNG draws.</para>
    ///
    /// <para>The structure is stored twice, by row and by column, because the encoder walks
    /// rows and the decoder walks the rows containing a newly recovered symbol. Both index
    /// lists are sorted ascending.</para>
    /// </summary>
    public sealed class ParityCheckMatrix
    {
        /// <summary>The default profile value for <c>N1</c>.</summary>
        public const int DefaultLeftDegree = 7;

        // Internal: LdpcStaircaseCodec reads these directly in its hot loops. The public
        // accessors below hand out clones so callers cannot corrupt the matrix.
        internal readonly int[][] RowColumnsInternal;
        internal readonly int[][] ColumnRowsInternal;

        private ParityCheckMatrix(int sourceSymbolCount, int repairSymbolCount, int leftDegree,
            int seed, int[][] rowColumns, int[][] columnRows, int entryCount)
        {
            SourceSymbolCount = sourceSymbolCount;
            RepairSymbolCount = repairSymbolCount;
            LeftDegree = leftDegree;
            Seed = seed;
            RowColumnsInternal = rowColumns;
            ColumnRowsInternal = columnRows;
            EntryCount = entryCount;
        }

        /// <summary>Number of source symbols, <c>K</c>.</summary>
        public int SourceSymbolCount { get; }

        /// <summary>Number of repair symbols, <c>R</c>. Also the number of equations (rows).</summary>
        public int RepairSymbolCount { get; }

        /// <summary>The configured number of ones per source column, <c>N1</c>.</summary>
        public int LeftDegree { get; }

        /// <summary>The PRNG seed the matrix was built from.</summary>
        public int Seed { get; }

        /// <summary>Total number of ones in <c>H</c>.</summary>
        public int EntryCount { get; }

        /// <summary>Number of encoding symbols, <c>N = K + R</c>. Also the number of columns.</summary>
        public int EncodingSymbolCount => SourceSymbolCount + RepairSymbolCount;

        /// <summary>The column indices of the ones in <paramref name="row"/>, ascending. Returns a copy.</summary>
        public int[] RowColumns(int row) => (int[])RowColumnsInternal[row].Clone();

        /// <summary>The row indices of the ones in <paramref name="column"/>, ascending. Returns a copy.</summary>
        public int[] ColumnRows(int column) => (int[])ColumnRowsInternal[column].Clone();

        /// <summary>Number of ones in <paramref name="row"/>.</summary>
        public int RowDegree(int row) => RowColumnsInternal[row].Length;

        /// <summary>Number of ones in <paramref name="column"/>.</summary>
        public int ColumnDegree(int column) => ColumnRowsInternal[column].Length;

        /// <summary>
        /// Builds <c>H</c> for <c>(K, R, N1, seed)</c> following SPEC.md section 4.
        /// </summary>
        /// <param name="sourceSymbolCount"><c>K</c>, at least 1.</param>
        /// <param name="repairSymbolCount">
        /// <c>R</c>, at least 0 (<c>0</c> yields the empty matrix — the no-repair bypass).
        /// </param>
        /// <param name="leftDegree"><c>N1</c>, at least 1 and at most <c>R</c> when <c>R &gt;= 1</c>.</param>
        /// <param name="seed">The PRNG seed, in <c>[1, 2147483646]</c>.</param>
        public static ParityCheckMatrix Build(int sourceSymbolCount, int repairSymbolCount,
            int leftDegree, int seed)
        {
            Validate(sourceSymbolCount, repairSymbolCount, leftDegree, seed);

            int k = sourceSymbolCount;
            int r = repairSymbolCount;
            int n = k + r;

            var rows = new List<int>[r];
            for (int i = 0; i < r; i++)
            {
                rows[i] = new List<int>();
            }

            var columns = new List<int>[n];
            for (int c = 0; c < n; c++)
            {
                columns[c] = new List<int>();
            }

            if (r > 0)
            {
                var rng = new PmmsRandom(seed);
                FillLeftPart(rows, columns, k, r, leftDegree, rng);
                RaiseLowDegreeRows(rows, columns, k, r, rng);
                FillStaircase(rows, columns, k, r);
            }

            int entries = 0;
            var rowColumns = new int[r][];
            for (int i = 0; i < r; i++)
            {
                rowColumns[i] = ToSortedArray(rows[i]);
                entries += rowColumns[i].Length;
            }

            var columnRows = new int[n][];
            for (int c = 0; c < n; c++)
            {
                columnRows[c] = ToSortedArray(columns[c]);
            }

            return new ParityCheckMatrix(k, r, leftDegree, seed, rowColumns, columnRows, entries);
        }

        /// <summary>
        /// SPEC.md section 4.2 — place <c>N1</c> ones in every source column.
        ///
        /// <para>The pool <c>u</c> holds <c>N1 * K</c> row indices cycling over <c>0..R-1</c>
        /// so that draws are spread evenly over the equations; <c>t</c> separates the consumed
        /// prefix from the still-available suffix. Drawing from the suffix and swap-removing
        /// the chosen slot is what keeps row degrees balanced.</para>
        /// </summary>
        private static void FillLeftPart(List<int>[] rows, List<int>[] columns, int k, int r, int n1,
            PmmsRandom rng)
        {
            int pool = n1 * k;
            var u = new int[pool];
            for (int h = pool - 1; h >= 0; h--)
            {
                u[h] = h % r;
            }

            int t = 0;
            for (int j = 0; j < k; j++)
            {
                for (int h = 0; h < n1; h++)
                {
                    // Does any unconsumed pool slot still name a row this column lacks?
                    // Scanning first is what guarantees the draw loop below terminates.
                    int i = t;
                    while (i < pool && columns[j].Contains(u[i]))
                    {
                        i++;
                    }

                    if (i < pool)
                    {
                        do
                        {
                            i = t + rng.NextBounded(pool - t);
                        }
                        while (columns[j].Contains(u[i]));

                        Set(rows, columns, u[i], j);
                        u[i] = u[t]; // swap-remove: u[t] has never been chosen
                        t++;
                    }
                    else
                    {
                        // The pool is exhausted for this column; fall back to a free row.
                        // Terminates because N1 <= R, so some row is always free.
                        int row;
                        do
                        {
                            row = rng.NextBounded(r);
                        }
                        while (columns[j].Contains(row));

                        Set(rows, columns, row, j);
                    }
                }
            }
        }

        /// <summary>
        /// SPEC.md section 4.3 — raise rows with fewer than two ones in the left part. Such
        /// rows are pathological for the decoder. The two checks are sequential, not exclusive:
        /// a degree-0 row is raised to 1 by the first and to 2 by the second.
        ///
        /// <para>Row degrees here count left-part entries only, which is automatic: the
        /// staircase has not been added yet.</para>
        ///
        /// <para>The <c>k &gt;= 2</c> guard is this specification's degenerate-case rule;
        /// RFC 5170 assumes a second distinct column always exists.</para>
        /// </summary>
        private static void RaiseLowDegreeRows(List<int>[] rows, List<int>[] columns, int k, int r,
            PmmsRandom rng)
        {
            for (int i = 0; i < r; i++)
            {
                if (rows[i].Count == 0)
                {
                    Set(rows, columns, i, rng.NextBounded(k));
                }

                if (rows[i].Count == 1 && k >= 2)
                {
                    int j;
                    do
                    {
                        j = rng.NextBounded(k);
                    }
                    while (columns[j].Contains(i));

                    Set(rows, columns, i, j);
                }
            }
        }

        /// <summary>
        /// SPEC.md section 4.4 — the staircase over the repair columns. Row <c>i</c> holds
        /// repair symbol <c>i</c> and (except for row 0) repair symbol <c>i - 1</c>, which is
        /// what makes encoding a running XOR. Consumes no PRNG draws.
        /// </summary>
        private static void FillStaircase(List<int>[] rows, List<int>[] columns, int k, int r)
        {
            Set(rows, columns, 0, k);
            for (int i = 1; i < r; i++)
            {
                Set(rows, columns, i, k + i);
                Set(rows, columns, i, k + i - 1);
            }
        }

        private static void Set(List<int>[] rows, List<int>[] columns, int row, int column)
        {
            rows[row].Add(column);
            columns[column].Add(row);
        }

        private static int[] ToSortedArray(List<int> values)
        {
            int[] result = values.ToArray();
            Array.Sort(result);
            return result;
        }

        private static void Validate(int sourceSymbolCount, int repairSymbolCount, int leftDegree,
            int seed)
        {
            if (sourceSymbolCount < 1)
            {
                throw new LdpcStaircaseException(
                    "sourceSymbolCount (K) must be >= 1; got " + sourceSymbolCount + ".");
            }

            if (repairSymbolCount < 0)
            {
                throw new LdpcStaircaseException(
                    "repairSymbolCount (R) must be >= 0; got " + repairSymbolCount + ".");
            }

            if ((long)sourceSymbolCount + repairSymbolCount > int.MaxValue)
            {
                throw new LdpcStaircaseException(
                    "sourceSymbolCount + repairSymbolCount (K + R) must be <= " + int.MaxValue
                    + "; got " + ((long)sourceSymbolCount + repairSymbolCount) + ".");
            }

            if (leftDegree < 1)
            {
                throw new LdpcStaircaseException("leftDegree (N1) must be >= 1; got " + leftDegree + ".");
            }

            if (repairSymbolCount >= 1 && leftDegree > repairSymbolCount)
            {
                // Each source column needs N1 *distinct* rows; without this the RFC
                // construction cannot terminate.
                throw new LdpcStaircaseException(
                    "leftDegree (N1) must be <= repairSymbolCount (R) when R >= 1; got N1="
                    + leftDegree + ", R=" + repairSymbolCount + ".");
            }

            if ((long)leftDegree * sourceSymbolCount > int.MaxValue)
            {
                throw new LdpcStaircaseException(
                    "leftDegree * sourceSymbolCount (N1 * K) must be <= " + int.MaxValue
                    + "; got " + ((long)leftDegree * sourceSymbolCount) + ".");
            }

            if (seed < PmmsRandom.MinSeed || seed > PmmsRandom.MaxSeed)
            {
                throw new LdpcStaircaseException(
                    "seed must be in [" + PmmsRandom.MinSeed + ", " + PmmsRandom.MaxSeed + "]; got "
                    + seed + ".");
            }
        }

        /// <summary>
        /// The canonical serialization of SPEC.md section 7.1: big-endian <c>u32</c> row count
        /// and column count, then per row its degree followed by its ascending column indices.
        /// This is the byte form the <c>matrix</c> conformance vectors compare.
        /// </summary>
        public byte[] Serialize()
        {
            var output = new byte[4 * (2 + RepairSymbolCount + EntryCount)];
            int position = 0;
            position = WriteU32(output, position, RepairSymbolCount);
            position = WriteU32(output, position, EncodingSymbolCount);
            for (int i = 0; i < RepairSymbolCount; i++)
            {
                int[] columns = RowColumnsInternal[i];
                position = WriteU32(output, position, columns.Length);
                for (int idx = 0; idx < columns.Length; idx++)
                {
                    position = WriteU32(output, position, columns[idx]);
                }
            }

            return output;
        }

        private static int WriteU32(byte[] output, int offset, int value)
        {
            output[offset] = (byte)(value >> 24);
            output[offset + 1] = (byte)(value >> 16);
            output[offset + 2] = (byte)(value >> 8);
            output[offset + 3] = (byte)value;
            return offset + 4;
        }

        public override string ToString()
        {
            return "ParityCheckMatrix{K=" + SourceSymbolCount + ", R=" + RepairSymbolCount
                + ", N1=" + LeftDegree + ", seed=" + Seed + ", entries=" + EntryCount + "}";
        }
    }
}
