using System;

namespace Erasure16
{
    /// <summary>
    /// Builds the systematic Cauchy generator matrix described in SPEC.md section 4 and
    /// inverts arbitrary K x K submatrices of present rows by Gaussian elimination over
    /// GF(2^16) (SPEC.md section 6).
    ///
    /// The N x K generator matrix G (N = K + R) is:
    ///   - rows 0..K-1: the K x K identity (data shards pass through unchanged), and
    ///   - parity row K+p (p in 0..R-1):
    ///         G[K+p][j] = inv(X_p XOR Y_j),  X_p = p,  Y_j = R + j,  j in 0..K-1.
    /// X = {0..R-1} and Y = {R..R+K-1} are disjoint, so X_p XOR Y_j is never 0.
    /// </summary>
    internal static class CauchyMatrix
    {
        /// <summary>
        /// Returns the generator row for shard index <paramref name="row"/> in
        /// <c>0..K+R-1</c>: an identity row for a data shard, or a Cauchy row for a
        /// parity shard. The returned array has length <paramref name="k"/>.
        /// </summary>
        internal static ushort[] GeneratorRow(int row, int k, int r)
        {
            var result = new ushort[k];

            if (row < k)
            {
                // Identity row for data shard `row`.
                result[row] = 1;
                return result;
            }

            // Parity row: p = row - k, in 0..R-1.
            int p = row - k;
            int xp = p;            // X_p = p
            for (int j = 0; j < k; j++)
            {
                int yj = r + j;    // Y_j = R + j
                result[j] = Gf16.Inv((ushort)(xp ^ yj));
            }

            return result;
        }

        /// <summary>
        /// The generator coefficient of parity row <paramref name="p"/> (in <c>0..R-1</c>) at
        /// data column <paramref name="j"/> (in <c>0..K-1</c>):
        /// <c>inv(X_p XOR Y_j) = inv(p XOR (R + j))</c> (SPEC.md section 4). This is the single
        /// Cauchy entry the submatrix decoder reads to build its <c>E x E</c> system without
        /// materializing the full generator.
        /// </summary>
        internal static ushort ParityCoeff(int p, int j, int r)
        {
            return Gf16.Inv((ushort)(p ^ (r + j)));
        }

        /// <summary>
        /// Builds the K x K matrix whose rows are the generator rows for the given
        /// <paramref name="rowIndices"/> (shard indices). <paramref name="rowIndices"/>
        /// must have exactly K entries. Returns a row-major K x K array.
        /// </summary>
        internal static ushort[][] BuildSquare(int[] rowIndices, int k, int r)
        {
            var matrix = new ushort[k][];
            for (int i = 0; i < k; i++)
            {
                matrix[i] = GeneratorRow(rowIndices[i], k, r);
            }

            return matrix;
        }

        /// <summary>
        /// Inverts a square K x K matrix over GF(2^16) by Gauss-Jordan elimination.
        /// The input is not mutated. Throws <see cref="Erasure16Exception"/> if the
        /// matrix is singular (which cannot happen for a Cauchy submatrix of distinct
        /// rows, but is guarded defensively).
        /// </summary>
        internal static ushort[][] Invert(ushort[][] matrix, int k)
        {
            return Invert(matrix, k, null);
        }

        /// <summary>
        /// Inverts a square K x K matrix over GF(2^16), invoking <paramref name="onColumnDone"/>
        /// (if non-null) with the 0-based column index after each of the <paramref name="k"/>
        /// elimination columns completes. The callback lets a caller report smooth progress
        /// through the <c>O(k^3)</c> inversion, which is otherwise a single opaque step.
        /// </summary>
        internal static ushort[][] Invert(ushort[][] matrix, int k, Action<int> onColumnDone)
        {
            // Work on a copy of the input augmented with the identity on the right.
            var a = new ushort[k][];
            var inv = new ushort[k][];
            for (int i = 0; i < k; i++)
            {
                a[i] = (ushort[])matrix[i].Clone();
                inv[i] = new ushort[k];
                inv[i][i] = 1;
            }

            for (int col = 0; col < k; col++)
            {
                // Find a pivot row at or below `col` with a non-zero entry in `col`.
                int pivot = -1;
                for (int row = col; row < k; row++)
                {
                    if (a[row][col] != 0)
                    {
                        pivot = row;
                        break;
                    }
                }

                if (pivot < 0)
                {
                    throw new Erasure16Exception(
                        "Reconstruction matrix is singular at column " + col +
                        "; the selected shard rows are not invertible.");
                }

                if (pivot != col)
                {
                    var tmp = a[pivot]; a[pivot] = a[col]; a[col] = tmp;
                    var tmpInv = inv[pivot]; inv[pivot] = inv[col]; inv[col] = tmpInv;
                }

                // Normalize the pivot row so that a[col][col] == 1.
                ushort pivVal = a[col][col];
                if (pivVal != 1)
                {
                    ushort pivInv = Gf16.Inv(pivVal);
                    ushort[] aRow = a[col];
                    ushort[] iRow = inv[col];
                    for (int j = 0; j < k; j++)
                    {
                        aRow[j] = Gf16.Mul(aRow[j], pivInv);
                        iRow[j] = Gf16.Mul(iRow[j], pivInv);
                    }
                }

                // Eliminate `col` from every other row.
                for (int row = 0; row < k; row++)
                {
                    if (row == col)
                    {
                        continue;
                    }

                    ushort factor = a[row][col];
                    if (factor == 0)
                    {
                        continue;
                    }

                    ushort[] aRow = a[row];
                    ushort[] iRow = inv[row];
                    ushort[] aPiv = a[col];
                    ushort[] iPiv = inv[col];
                    for (int j = 0; j < k; j++)
                    {
                        // row[j] ^= factor * pivot[j]  (subtraction == XOR in GF(2^m))
                        aRow[j] ^= Gf16.Mul(factor, aPiv[j]);
                        iRow[j] ^= Gf16.Mul(factor, iPiv[j]);
                    }
                }

                onColumnDone?.Invoke(col);
            }

            return inv;
        }
    }
}
