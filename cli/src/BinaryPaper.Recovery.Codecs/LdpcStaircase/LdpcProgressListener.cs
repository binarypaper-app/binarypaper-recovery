namespace LdpcStaircase
{
    /// <summary>
    /// Optional callback invoked during <see cref="LdpcStaircaseCodec.Decode(byte[][], bool[], DecodeOptions)"/>
    /// so a caller (for example a UI) can render a real progress bar instead of an
    /// indeterminate spinner.
    ///
    /// <para><paramref name="completedUnits"/> rises monotonically from <c>0</c> toward
    /// <paramref name="totalUnits"/> over one decode call. Both are abstract "work units" —
    /// an internal estimate of the decode's cost, spanning the peeling pass and, when it is
    /// needed, the residual solve — so treat the ratio <c>completedUnits / totalUnits</c> as
    /// the fraction complete and do not ascribe an absolute unit to the numbers.
    /// <paramref name="totalUnits"/> is constant within a single call and always <c>&gt; 0</c>
    /// when the callback fires; the final invocation reports
    /// <c>completedUnits == totalUnits</c>.</para>
    ///
    /// <para>The estimate reserves room for a residual solve up front, because whether one is
    /// needed is only known after peeling reaches its fixed point. A decode that peels cleanly
    /// therefore jumps from the peeling fraction straight to complete — the common and fastest
    /// case.</para>
    ///
    /// <para>The callback runs inline on the calling thread, so keep it cheap (post to a UI
    /// thread rather than doing work here). To <b>cancel</b> a long decode, throw an exception
    /// from the callback: it propagates out of the decode call. The symbol array may be left
    /// partially recovered in that case and should be discarded.</para>
    /// </summary>
    /// <param name="completedUnits">
    /// Work completed so far, in <c>[0, totalUnits]</c>, monotonic within one decode call.
    /// </param>
    /// <param name="totalUnits">Total work for this decode call; constant per call, <c>&gt; 0</c>.</param>
    public delegate void ProgressListener(long completedUnits, long totalUnits);
}
