namespace Erasure16
{
    /// <summary>
    /// Optional callback invoked during a long <see cref="ReedSolomon16.Reconstruct(byte[][], bool[], ProgressListener)"/>
    /// / <see cref="ReedSolomon16.ReconstructData(byte[][], bool[], ProgressListener)"/> so a caller
    /// (for example a UI) can render a real progress bar instead of an indeterminate spinner.
    ///
    /// <para><paramref name="completedUnits"/> rises monotonically from <c>0</c> toward
    /// <paramref name="totalUnits"/> over one reconstruct call. Both are abstract "work units" —
    /// an internal estimate of field-multiply cost summed across the decode's phases (building the
    /// system, inverting it, and the per-word solve) — so treat the ratio
    /// <c>completedUnits / totalUnits</c> as the fraction complete and do not ascribe an absolute
    /// unit to the numbers. <paramref name="totalUnits"/> is constant within a single call and
    /// always <c>&gt; 0</c> when the callback fires; the final invocation reports
    /// <c>completedUnits == totalUnits</c>.</para>
    ///
    /// <para>Reconstruction only reports progress when it actually runs the erasure math (at least
    /// one <em>data</em> shard was missing). The trivial cases — nothing missing, or only parity
    /// missing — complete without meaningful work and simply report a single
    /// <c>onProgress(total, total)</c>.</para>
    ///
    /// <para>The callback runs inline on the calling thread, so keep it cheap (post to a UI thread
    /// rather than doing work here). To <b>cancel</b> a long decode, throw an exception from the
    /// callback: it propagates out of the reconstruct call. The shard array may be left partially
    /// reconstructed in that case and should be discarded.</para>
    /// </summary>
    /// <param name="completedUnits">
    /// Work completed so far, in <c>[0, totalUnits]</c>, monotonic within one reconstruct call.
    /// </param>
    /// <param name="totalUnits">
    /// Total work for this reconstruct call; constant per call, <c>&gt; 0</c>.
    /// </param>
    public delegate void ProgressListener(long completedUnits, long totalUnits);
}
