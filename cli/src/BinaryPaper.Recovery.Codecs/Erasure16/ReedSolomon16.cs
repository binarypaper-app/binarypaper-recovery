using System;

namespace Erasure16
{
    /// <summary>
    /// Systematic Reed-Solomon erasure coder over GF(2^16) with a Cauchy generator
    /// matrix (SPEC.md). Given <c>K</c> equal-length data shards it produces <c>R</c>
    /// parity shards such that the original data can be reconstructed from any <c>K</c>
    /// of the <c>K + R</c> shards.
    ///
    /// The coder is deliberately abstract: shard byte arrays in, shard byte arrays out.
    /// It performs no I/O, compression, or encryption. Shards are read as 16-bit field
    /// elements in big-endian: <c>word[w] = (shard[2w] &lt;&lt; 8) | shard[2w+1]</c>.
    /// </summary>
    public sealed class ReedSolomon16
    {
        /// <summary>The largest legal value of <c>K + R</c> (the field has 65536 elements).</summary>
        public const int MaxTotalShards = 65536;

        private readonly int _dataShardCount;
        private readonly int _parityShardCount;
        private readonly int _totalShardCount;

        private ReedSolomon16(int dataShardCount, int parityShardCount)
        {
            _dataShardCount = dataShardCount;
            _parityShardCount = parityShardCount;
            _totalShardCount = dataShardCount + parityShardCount;
        }

        /// <summary>Number of data shards, <c>K</c>.</summary>
        public int DataShardCount => _dataShardCount;

        /// <summary>Number of parity shards, <c>R</c>.</summary>
        public int ParityShardCount => _parityShardCount;

        /// <summary>Total number of shards, <c>N = K + R</c>.</summary>
        public int TotalShardCount => _totalShardCount;

        /// <summary>
        /// Creates a coder for <paramref name="dataShardCount"/> (<c>K</c>) data shards
        /// and <paramref name="parityShardCount"/> (<c>R</c>) parity shards.
        /// Requires <c>K &gt;= 1</c>, <c>R &gt;= 0</c>, and <c>K + R &lt;= 65536</c>.
        /// </summary>
        /// <exception cref="Erasure16Exception">If the parameters are out of range.</exception>
        public static ReedSolomon16 Create(int dataShardCount, int parityShardCount)
        {
            if (dataShardCount < 1)
            {
                throw new Erasure16Exception(
                    "dataShardCount (K) must be >= 1; got " + dataShardCount + ".");
            }

            if (parityShardCount < 0)
            {
                throw new Erasure16Exception(
                    "parityShardCount (R) must be >= 0; got " + parityShardCount + ".");
            }

            // Compute K + R in a way that cannot overflow for any int inputs.
            long total = (long)dataShardCount + parityShardCount;
            if (total > MaxTotalShards)
            {
                throw new Erasure16Exception(
                    "dataShardCount + parityShardCount (K + R) must be <= " + MaxTotalShards +
                    "; got " + total + ".");
            }

            return new ReedSolomon16(dataShardCount, parityShardCount);
        }

        /// <summary>
        /// Encodes in place. <paramref name="shards"/> must have <c>K + R</c> entries of
        /// equal, even, positive length; slots <c>[0..K)</c> hold the caller-supplied data
        /// and slots <c>[K..K+R)</c> are overwritten with the computed parity. With
        /// <c>R == 0</c> this only validates the data shards.
        /// </summary>
        /// <exception cref="Erasure16Exception">
        /// If the shard count, lengths, or parities are invalid.
        /// </exception>
        public void Encode(byte[][] shards)
        {
            int shardLength = ValidateShardArray(shards);
            int words = shardLength / 2;

            for (int p = 0; p < _parityShardCount; p++)
            {
                ushort[] row = CauchyMatrix.GeneratorRow(_dataShardCount + p, _dataShardCount, _parityShardCount);
                byte[] parity = shards[_dataShardCount + p];

                for (int w = 0; w < words; w++)
                {
                    ushort acc = 0;
                    int bi = w * 2;
                    for (int j = 0; j < _dataShardCount; j++)
                    {
                        byte[] data = shards[j];
                        ushort value = (ushort)((data[bi] << 8) | data[bi + 1]);
                        acc ^= Gf16.Mul(row[j], value);
                    }

                    parity[bi] = (byte)(acc >> 8);
                    parity[bi + 1] = (byte)(acc & 0xFF);
                }
            }
        }

        /// <summary>
        /// Reconstructs every missing shard in place. <paramref name="shardPresent"/>
        /// marks which of the <c>K + R</c> shards currently hold valid bytes; at least
        /// <c>K</c> must be present. Present shards are read for their length; missing
        /// shard slots are (re)allocated and filled. After this returns, all <c>K + R</c>
        /// shards are present.
        /// </summary>
        /// <exception cref="Erasure16Exception">
        /// If fewer than <c>K</c> shards are present, or lengths are invalid.
        /// </exception>
        public void Reconstruct(byte[][] shards, bool[] shardPresent)
        {
            ReconstructInternal(shards, shardPresent, dataOnly: false, RecoveryAlgorithm.Submatrix, listener: null);
        }

        /// <summary>
        /// Reconstructs every missing shard in place (see
        /// <see cref="Reconstruct(byte[][], bool[])"/>), reporting incremental progress to
        /// <paramref name="listener"/> while the erasure math runs so a caller can drive a real
        /// progress bar. <paramref name="listener"/> may be <c>null</c> (equivalent to the
        /// two-argument form).
        /// </summary>
        /// <exception cref="Erasure16Exception">
        /// If fewer than <c>K</c> shards are present, or lengths are invalid.
        /// </exception>
        public void Reconstruct(byte[][] shards, bool[] shardPresent, ProgressListener listener)
        {
            ReconstructInternal(shards, shardPresent, dataOnly: false, RecoveryAlgorithm.Submatrix, listener);
        }

        /// <summary>
        /// Reconstructs only the missing data shards (slots <c>[0..K)</c>) in place,
        /// leaving any missing parity shards untouched. At least <c>K</c> shards must be
        /// present.
        /// </summary>
        /// <exception cref="Erasure16Exception">
        /// If fewer than <c>K</c> shards are present, or lengths are invalid.
        /// </exception>
        public void ReconstructData(byte[][] shards, bool[] shardPresent)
        {
            ReconstructInternal(shards, shardPresent, dataOnly: true, RecoveryAlgorithm.Submatrix, listener: null);
        }

        /// <summary>
        /// Reconstructs only the missing data shards in place (see
        /// <see cref="ReconstructData(byte[][], bool[])"/>), reporting incremental progress to
        /// <paramref name="listener"/> while the erasure math runs. <paramref name="listener"/>
        /// may be <c>null</c> (equivalent to the two-argument form).
        /// </summary>
        /// <exception cref="Erasure16Exception">
        /// If fewer than <c>K</c> shards are present, or lengths are invalid.
        /// </exception>
        public void ReconstructData(byte[][] shards, bool[] shardPresent, ProgressListener listener)
        {
            ReconstructInternal(shards, shardPresent, dataOnly: true, RecoveryAlgorithm.Submatrix, listener);
        }

        // Internal seam: force a specific recovery algorithm. Production always uses
        // Submatrix (the two- and three-argument public methods); the differential tests and
        // the benchmark use this to drive the legacy FullMatrix path for comparison.
        internal void Reconstruct(byte[][] shards, bool[] shardPresent, RecoveryAlgorithm algorithm)
        {
            ReconstructInternal(shards, shardPresent, dataOnly: false, algorithm, listener: null);
        }

        internal void ReconstructData(byte[][] shards, bool[] shardPresent, RecoveryAlgorithm algorithm)
        {
            ReconstructInternal(shards, shardPresent, dataOnly: true, algorithm, listener: null);
        }

        /// <summary>
        /// Selects how <see cref="ReconstructData(byte[][], bool[])"/> /
        /// <see cref="Reconstruct(byte[][], bool[])"/> recover missing <em>data</em> shards. Both
        /// produce byte-identical output (the recovered data is the unique solution of the erasure
        /// system); they differ only in cost.
        /// </summary>
        internal enum RecoveryAlgorithm
        {
            /// <summary>
            /// Legacy path: invert the full <c>K x K</c> reconstruction matrix (<c>O(K^3)</c> time,
            /// <c>O(K^2)</c> memory), independent of how many shards were lost. Retained as the
            /// differential-test oracle and benchmark baseline.
            /// </summary>
            FullMatrix,

            /// <summary>
            /// Default path: solve only the <c>E x E</c> system for the <c>E</c> missing data shards
            /// (<c>O(E^3 + E*K*words)</c> time, <c>O(E*K)</c> memory). Equal to
            /// <see cref="FullMatrix"/> only in the degenerate case <c>E == K</c> (all data lost).
            /// </summary>
            Submatrix
        }

        /// <summary>
        /// Returns <c>true</c> if all <c>K + R</c> shards are mutually consistent — i.e.
        /// re-encoding the data shards reproduces the existing parity shards exactly.
        /// Expects a fully populated shard array of equal, even, positive lengths.
        /// </summary>
        /// <exception cref="Erasure16Exception">If the shard array is structurally invalid.</exception>
        public bool Verify(byte[][] shards)
        {
            int shardLength = ValidateShardArray(shards);
            int words = shardLength / 2;

            for (int p = 0; p < _parityShardCount; p++)
            {
                ushort[] row = CauchyMatrix.GeneratorRow(_dataShardCount + p, _dataShardCount, _parityShardCount);
                byte[] parity = shards[_dataShardCount + p];

                for (int w = 0; w < words; w++)
                {
                    ushort acc = 0;
                    int bi = w * 2;
                    for (int j = 0; j < _dataShardCount; j++)
                    {
                        byte[] data = shards[j];
                        ushort value = (ushort)((data[bi] << 8) | data[bi + 1]);
                        acc ^= Gf16.Mul(row[j], value);
                    }

                    byte hi = (byte)(acc >> 8);
                    byte lo = (byte)(acc & 0xFF);
                    if (parity[bi] != hi || parity[bi + 1] != lo)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Splits <paramref name="data"/> into <paramref name="dataShardCount"/> zero-padded
        /// shards of <paramref name="shardSize"/> bytes each (SPEC.md section 7).
        /// <paramref name="shardSize"/> must be even and positive, and
        /// <c>data.Length &lt;= dataShardCount * shardSize</c>.
        /// </summary>
        /// <exception cref="Erasure16Exception">If the arguments are out of range.</exception>
        public static byte[][] Split(byte[] data, int dataShardCount, int shardSize)
        {
            if (data == null)
            {
                throw new Erasure16Exception("split: data must not be null.");
            }

            if (dataShardCount < 1)
            {
                throw new Erasure16Exception("split: dataShardCount (K) must be >= 1; got " + dataShardCount + ".");
            }

            if (shardSize <= 0 || (shardSize & 1) != 0)
            {
                throw new Erasure16Exception("split: shardSize must be even and > 0; got " + shardSize + ".");
            }

            long capacity = (long)dataShardCount * shardSize;
            if (data.Length > capacity)
            {
                throw new Erasure16Exception(
                    "split: data length " + data.Length + " exceeds capacity " + capacity +
                    " (dataShardCount * shardSize).");
            }

            var shards = new byte[dataShardCount][];
            for (int i = 0; i < dataShardCount; i++)
            {
                var shard = new byte[shardSize];
                int offset = i * shardSize;
                int remaining = data.Length - offset;
                if (remaining > 0)
                {
                    int copy = remaining < shardSize ? remaining : shardSize;
                    Array.Copy(data, offset, shard, 0, copy);
                }

                shards[i] = shard;
            }

            return shards;
        }

        /// <summary>
        /// Concatenates <paramref name="dataShards"/> in order and returns the first
        /// <paramref name="outputLength"/> bytes (SPEC.md section 7).
        /// </summary>
        /// <exception cref="Erasure16Exception">
        /// If <paramref name="outputLength"/> is negative or exceeds the concatenated length.
        /// </exception>
        public static byte[] Join(byte[][] dataShards, int outputLength)
        {
            if (dataShards == null)
            {
                throw new Erasure16Exception("join: dataShards must not be null.");
            }

            if (outputLength < 0)
            {
                throw new Erasure16Exception("join: outputLength must be >= 0; got " + outputLength + ".");
            }

            long total = 0;
            for (int i = 0; i < dataShards.Length; i++)
            {
                if (dataShards[i] == null)
                {
                    throw new Erasure16Exception("join: dataShards[" + i + "] must not be null.");
                }

                total += dataShards[i].Length;
            }

            if (outputLength > total)
            {
                throw new Erasure16Exception(
                    "join: outputLength " + outputLength + " exceeds concatenated length " + total + ".");
            }

            var output = new byte[outputLength];
            int written = 0;
            for (int i = 0; i < dataShards.Length && written < outputLength; i++)
            {
                byte[] shard = dataShards[i];
                int copy = shard.Length;
                if (written + copy > outputLength)
                {
                    copy = outputLength - written;
                }

                Array.Copy(shard, 0, output, written, copy);
                written += copy;
            }

            return output;
        }

        private void ReconstructInternal(byte[][] shards, bool[] shardPresent, bool dataOnly,
            RecoveryAlgorithm algorithm, ProgressListener listener)
        {
            if (shards == null)
            {
                throw new Erasure16Exception("reconstruct: shards must not be null.");
            }

            if (shardPresent == null)
            {
                throw new Erasure16Exception("reconstruct: shardPresent must not be null.");
            }

            if (shards.Length != _totalShardCount)
            {
                throw new Erasure16Exception(
                    "reconstruct: expected " + _totalShardCount + " shards (K + R); got " + shards.Length + ".");
            }

            if (shardPresent.Length != _totalShardCount)
            {
                throw new Erasure16Exception(
                    "reconstruct: shardPresent must have " + _totalShardCount + " entries; got " +
                    shardPresent.Length + ".");
            }

            // Determine the shard length from the present shards and validate them.
            int shardLength = -1;
            int presentCount = 0;
            for (int i = 0; i < _totalShardCount; i++)
            {
                if (!shardPresent[i])
                {
                    continue;
                }

                byte[] shard = shards[i];
                if (shard == null)
                {
                    throw new Erasure16Exception(
                        "reconstruct: shard " + i + " is marked present but is null.");
                }

                if (shardLength < 0)
                {
                    shardLength = shard.Length;
                    if (shardLength <= 0 || (shardLength & 1) != 0)
                    {
                        throw new Erasure16Exception(
                            "reconstruct: shard length must be even and > 0; got " + shardLength + ".");
                    }
                }
                else if (shard.Length != shardLength)
                {
                    throw new Erasure16Exception(
                        "reconstruct: present shards have differing lengths (" + shardLength + " vs " +
                        shard.Length + ").");
                }

                presentCount++;
            }

            if (presentCount < _dataShardCount)
            {
                throw new Erasure16Exception(
                    "reconstruct: need at least " + _dataShardCount + " present shards (K); got " +
                    presentCount + ".");
            }

            int words = shardLength / 2;

            // Are all K data shards present? If so reconstruction of data is trivial
            // (systematic code); only missing parity needs recomputing.
            bool allDataPresent = true;
            for (int j = 0; j < _dataShardCount; j++)
            {
                if (!shardPresent[j])
                {
                    allDataPresent = false;
                    break;
                }
            }

            if (!allDataPresent)
            {
                if (algorithm == RecoveryAlgorithm.FullMatrix)
                {
                    RecoverDataFullMatrix(shards, shardPresent, words, shardLength);
                }
                else
                {
                    RecoverDataSubmatrix(shards, shardPresent, words, shardLength, listener);
                }
            }
            else if (listener != null)
            {
                // No data missing: nothing to solve. Report a single completed tick.
                listener(1, 1);
            }

            // At this point all K data shards are present (either originally or just
            // recovered). If requested, also fill any missing parity shards by re-encoding.
            if (!dataOnly)
            {
                for (int p = 0; p < _parityShardCount; p++)
                {
                    int idx = _dataShardCount + p;
                    if (shardPresent[idx])
                    {
                        continue;
                    }

                    ushort[] row = CauchyMatrix.GeneratorRow(idx, _dataShardCount, _parityShardCount);
                    byte[] parity = EnsureShard(shards, idx, shardLength);

                    for (int w = 0; w < words; w++)
                    {
                        ushort acc = 0;
                        int bi = w * 2;
                        for (int j = 0; j < _dataShardCount; j++)
                        {
                            byte[] data = shards[j];
                            ushort value = (ushort)((data[bi] << 8) | data[bi + 1]);
                            acc ^= Gf16.Mul(row[j], value);
                        }

                        parity[bi] = (byte)(acc >> 8);
                        parity[bi + 1] = (byte)(acc & 0xFF);
                    }

                    shardPresent[idx] = true;
                }
            }
        }

        /// <summary>
        /// Legacy recovery: select K present shards, invert their full <c>K x K</c> generator
        /// submatrix, and apply it to the selected shard words. Cost is <c>O(K^3)</c> time and
        /// <c>O(K^2)</c> memory regardless of how few shards were lost. Retained as the
        /// differential-test oracle and benchmark baseline for <see cref="RecoverDataSubmatrix"/>;
        /// see <see cref="RecoveryAlgorithm"/>. Assumes validation has already run and that at
        /// least one data shard is missing.
        /// </summary>
        private void RecoverDataFullMatrix(byte[][] shards, bool[] shardPresent, int words, int shardLength)
        {
            int k = _dataShardCount;

            // Select the first K present shards (any K distinct rows are invertible).
            var selected = new int[k];
            int count = 0;
            for (int i = 0; i < _totalShardCount && count < k; i++)
            {
                if (shardPresent[i])
                {
                    selected[count++] = i;
                }
            }

            // M is the K x K matrix of the selected shards' generator rows; data = M^-1 * selected.
            ushort[][] m = CauchyMatrix.BuildSquare(selected, k, _parityShardCount);
            ushort[][] mInv = CauchyMatrix.Invert(m, k);

            // Snapshot the selected shards' bytes (they are all present and validated).
            var selectedShards = new byte[k][];
            for (int i = 0; i < k; i++)
            {
                selectedShards[i] = shards[selected[i]];
            }

            // Which data shards are missing and therefore need writing?
            var missingData = new bool[k];
            for (int j = 0; j < k; j++)
            {
                if (!shardPresent[j])
                {
                    missingData[j] = true;
                    EnsureShard(shards, j, shardLength);
                }
            }

            for (int w = 0; w < words; w++)
            {
                int bi = w * 2;

                // Gather the selected shards' word values once per word index.
                var sel = new ushort[k];
                for (int i = 0; i < k; i++)
                {
                    byte[] s = selectedShards[i];
                    sel[i] = (ushort)((s[bi] << 8) | s[bi + 1]);
                }

                for (int row = 0; row < k; row++)
                {
                    if (!missingData[row])
                    {
                        continue; // Present data shards are already correct (systematic).
                    }

                    ushort[] invRow = mInv[row];
                    ushort acc = 0;
                    for (int i = 0; i < k; i++)
                    {
                        acc ^= Gf16.Mul(invRow[i], sel[i]);
                    }

                    byte[] dataShard = shards[row];
                    dataShard[bi] = (byte)(acc >> 8);
                    dataShard[bi + 1] = (byte)(acc & 0xFF);
                }
            }

            // Mark the recovered data shards present.
            for (int j = 0; j < k; j++)
            {
                if (missingData[j])
                {
                    shardPresent[j] = true;
                }
            }
        }

        /// <summary>
        /// Optimized recovery: solve only the <c>E x E</c> system for the <c>E</c> missing data
        /// shards, instead of inverting the full <c>K x K</c> reconstruction matrix.
        ///
        /// <para>Choose <c>E</c> present parity shards as equations. Each parity value is the
        /// generator-weighted sum over all <c>K</c> data shards; subtracting the (known)
        /// contribution of the <c>K - E</c> present data shards leaves an <c>E x E</c> Cauchy
        /// system in the <c>E</c> unknown (missing) data words. Cost is <c>O(E^3)</c> to invert
        /// plus <c>O(E*K*words)</c> to apply, and <c>O(E*K)</c> memory — versus the legacy
        /// <c>O(K^3)</c> time and <c>O(K^2)</c> memory of <see cref="RecoverDataFullMatrix"/>. The
        /// recovered bytes are identical (the erasure solution is unique); this only avoids work
        /// proportional to the shards that were <em>not</em> lost. It reduces to the legacy cost
        /// exactly when <c>E == K</c> (all data lost). Assumes validation has run and at least one
        /// data shard is missing.</para>
        /// </summary>
        private void RecoverDataSubmatrix(byte[][] shards, bool[] shardPresent, int words, int shardLength,
            ProgressListener listener)
        {
            int k = _dataShardCount;
            int r = _parityShardCount;

            // Partition data columns into present / missing.
            var missing = new int[k];
            var presentData = new int[k];
            int e = 0;
            int presentDataCount = 0;
            for (int j = 0; j < k; j++)
            {
                if (shardPresent[j])
                {
                    presentData[presentDataCount++] = j;
                }
                else
                {
                    missing[e++] = j;
                }
            }
            // e >= 1: the caller only invokes this when at least one data shard is missing.

            // Choose E present parity shards to supply E independent equations. There are always
            // at least E of them (presentCount >= K and only K - E data shards are present).
            var parityP = new int[e];
            int eq = 0;
            for (int p = 0; p < r && eq < e; p++)
            {
                if (shardPresent[k + p])
                {
                    parityP[eq++] = p;
                }
            }

            if (eq < e)
            {
                throw new Erasure16Exception(
                    "reconstruct: only " + eq + " present parity shards to recover " + e +
                    " missing data shards.");
            }

            // A[i][c] = coeff(parity p_i, missing data col c): the E x E system to invert.
            // B[i][c] = coeff(parity p_i, present data col c): folds the known data out of the RHS.
            var a = new ushort[e][];
            var b = new ushort[e][];
            for (int i = 0; i < e; i++)
            {
                int p = parityP[i];
                var arow = new ushort[e];
                for (int c = 0; c < e; c++)
                {
                    arow[c] = CauchyMatrix.ParityCoeff(p, missing[c], r);
                }

                a[i] = arow;

                var brow = new ushort[presentDataCount];
                for (int c = 0; c < presentDataCount; c++)
                {
                    brow[c] = CauchyMatrix.ParityCoeff(p, presentData[c], r);
                }

                b[i] = brow;
            }

            // Progress model (tracked only when a listener is attached): estimated field-op cost
            // across the build, invert, and per-word phases, so the reported fraction is roughly
            // proportional to real work no matter which phase dominates.
            long costBuild = (long)e * k;          // ~E*K coefficient lookups (A + B)
            long costInvertPerCol = 2L * e * e;    // ~2E^2 per Gauss-Jordan column
            long costInvert = costInvertPerCol * e; // ~2E^3 total
            long costPerWord = (long)e * k;        // knownSum E*(K-E) + solve E^2 = E*K
            Progress progress = listener == null
                ? null
                : new Progress(listener, costBuild + costInvert + (long)words * costPerWord);
            progress?.Set(costBuild);

            ushort[][] aInv = CauchyMatrix.Invert(a, e, progress == null
                ? (Action<int>)null
                : col => progress.Set(costBuild + (col + 1L) * costInvertPerCol));

            // Ensure the missing data shards have buffers to receive the solution.
            for (int c = 0; c < e; c++)
            {
                EnsureShard(shards, missing[c], shardLength);
            }

            // Resolve the shard byte arrays once (outside the per-word loop) so the hot loop
            // indexes flat locals rather than double-dereferencing through index arrays.
            var presentDataShards = new byte[presentDataCount][];
            for (int c = 0; c < presentDataCount; c++)
            {
                presentDataShards[c] = shards[presentData[c]];
            }

            var parityShards = new byte[e][];
            var missingShards = new byte[e][];
            for (int i = 0; i < e; i++)
            {
                parityShards[i] = shards[k + parityP[i]];
                missingShards[i] = shards[missing[i]];
            }

            long afterInvert = costBuild + costInvert;
            var pd = new ushort[presentDataCount]; // present-data words, gathered once per word
            var rhs = new ushort[e];
            for (int w = 0; w < words; w++)
            {
                int bi = w * 2;

                // Gather each present-data word once (it feeds every equation).
                for (int c = 0; c < presentDataCount; c++)
                {
                    byte[] d = presentDataShards[c];
                    pd[c] = (ushort)((d[bi] << 8) | d[bi + 1]);
                }

                // rhs_i = parity_{p_i}[w] XOR (sum over present data of B[i][c] * pd[c]).
                for (int i = 0; i < e; i++)
                {
                    ushort pv = ReadWord(parityShards[i], bi);
                    rhs[i] = (ushort)(pv ^ Dot(b[i], pd, presentDataCount));
                }

                // missing_c[w] = sum over i of aInv[c][i] * rhs_i.
                for (int c = 0; c < e; c++)
                {
                    ushort acc = Dot(aInv[c], rhs, e);
                    byte[] outShard = missingShards[c];
                    outShard[bi] = (byte)(acc >> 8);
                    outShard[bi + 1] = (byte)(acc & 0xFF);
                }

                progress?.Set(afterInvert + (w + 1L) * costPerWord);
            }

            // Mark the recovered data shards present.
            for (int c = 0; c < e; c++)
            {
                shardPresent[missing[c]] = true;
            }

            progress?.Done();
        }

        /// <summary>
        /// Throttled progress relay: forwards at most ~200 <see cref="ProgressListener"/> calls over
        /// one decode, clamps to <c>[0, total]</c>, and guarantees one final exact-total tick via
        /// <see cref="Done"/>.
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

        /// <summary>
        /// GF(2^16) dot product: XOR over <c>i in [0, len)</c> of <c>Mul(x[i], y[i])</c>. Four
        /// independent accumulators keep the XOR-reduction from being a single serial dependency
        /// chain; XOR is associative and commutative, so splitting and recombining is bit-identical
        /// to a straight left fold. (Measured effect is small — GF(2^16) decode is dominated by the
        /// multiply's exp/log table-load traffic rather than the reduction latency — but the shape
        /// is clean and does no harm.)
        /// </summary>
        private static ushort Dot(ushort[] x, ushort[] y, int len)
        {
            ushort a0 = 0;
            ushort a1 = 0;
            ushort a2 = 0;
            ushort a3 = 0;
            int i = 0;
            int limit = len - 3;
            for (; i < limit; i += 4)
            {
                a0 ^= Gf16.Mul(x[i], y[i]);
                a1 ^= Gf16.Mul(x[i + 1], y[i + 1]);
                a2 ^= Gf16.Mul(x[i + 2], y[i + 2]);
                a3 ^= Gf16.Mul(x[i + 3], y[i + 3]);
            }

            ushort acc = (ushort)(a0 ^ a1 ^ a2 ^ a3);
            for (; i < len; i++)
            {
                acc ^= Gf16.Mul(x[i], y[i]);
            }

            return acc;
        }

        /// <summary>Reads a big-endian 16-bit word from <paramref name="shard"/> at byte offset <paramref name="bi"/>.</summary>
        private static ushort ReadWord(byte[] shard, int bi)
        {
            return (ushort)((shard[bi] << 8) | shard[bi + 1]);
        }

        private static byte[] EnsureShard(byte[][] shards, int index, int shardLength)
        {
            byte[] shard = shards[index];
            if (shard == null || shard.Length != shardLength)
            {
                shard = new byte[shardLength];
                shards[index] = shard;
            }

            return shard;
        }

        /// <summary>
        /// Validates a fully populated shard array (used by Encode/Verify): exactly
        /// <c>K + R</c> non-null shards of equal, even, positive length. Returns the
        /// common shard length.
        /// </summary>
        private int ValidateShardArray(byte[][] shards)
        {
            if (shards == null)
            {
                throw new Erasure16Exception("shards must not be null.");
            }

            if (shards.Length != _totalShardCount)
            {
                throw new Erasure16Exception(
                    "expected " + _totalShardCount + " shards (K + R); got " + shards.Length + ".");
            }

            int shardLength = -1;
            for (int i = 0; i < _totalShardCount; i++)
            {
                byte[] shard = shards[i];
                if (shard == null)
                {
                    throw new Erasure16Exception("shard " + i + " must not be null.");
                }

                if (shardLength < 0)
                {
                    shardLength = shard.Length;
                    if (shardLength <= 0 || (shardLength & 1) != 0)
                    {
                        throw new Erasure16Exception(
                            "shard length must be even and > 0; got " + shardLength + ".");
                    }
                }
                else if (shard.Length != shardLength)
                {
                    throw new Erasure16Exception(
                        "all shards must have equal length; shard " + i + " has length " + shard.Length +
                        " but expected " + shardLength + ".");
                }
            }

            return shardLength;
        }
    }
}
