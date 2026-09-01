// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using ZXingCpp;

namespace BinaryPaper.Recovery.Images;

/// <summary>
/// Owns the managed/native lifetime boundary for one zxing-cpp read.
/// </summary>
/// <remarks>
/// <para><see cref="ImageView"/> is a non-owning native view over caller-owned pixels. Its managed
/// constructor passes a pointer to native code but does not pin the managed array for the later
/// <c>Read</c> call. Keeping the array alive is not enough: a live managed object may still move.</para>
///
/// <para>This helper pins the array for the complete native read, keeps the view and options alive
/// until the call and all result inspection finish, and disposes every returned native barcode.
/// Every recovery-side zxing-cpp read goes through this one boundary.</para>
/// </remarks>
internal static class NativeBarcodeReader
{
    public static void Read(
        byte[] pixels,
        int width,
        int height,
        ImageFormat format,
        ReaderOptions options,
        Action<Barcode> accept)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(accept);

        if (pixels.Length == 0)
        {
            return;
        }

        GCHandle pin = default;
        Barcode[] barcodes = [];
        ImageView? view = null;

        try
        {
            pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            view = new ImageView(pin.AddrOfPinnedObject(), width, height, format);

            barcodes = BarcodeReader.Read(view, options);
            foreach (Barcode barcode in barcodes)
            {
                accept(barcode);
            }
        }
        finally
        {
            foreach (Barcode barcode in barcodes)
            {
                barcode.Dispose();
            }

            // BarcodeReader.Read passes only the native pointers onward. These managed owners must
            // remain alive until native work and result inspection are both complete, including
            // exceptional paths.
            GC.KeepAlive(options);
            GC.KeepAlive(view);

            if (pin.IsAllocated)
            {
                pin.Free();
            }

            GC.KeepAlive(pixels);
        }
    }
}
