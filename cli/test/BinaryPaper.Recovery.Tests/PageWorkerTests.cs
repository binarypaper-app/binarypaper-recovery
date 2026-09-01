// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using BinaryPaper.Recovery.Cli;
using BinaryPaper.Recovery.Images;
using System.Runtime.InteropServices;
using Xunit;

namespace BinaryPaper.Recovery.Tests;

/// <summary>Process-boundary tests for native page decoding.</summary>
public sealed class PageWorkerTests
{
    [Fact]
    public void FailedChildDoesNotPoisonTheParentOrTheNextPage()
    {
        byte[] page = SyntheticPage.Photograph([MakePayload(17, 256)], tilt: 0, blur: 0);
        string path = Path.Combine(Path.GetTempPath(), $"binarypaper-worker-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, page);

        try
        {
            PageWorker.WorkerLaunch launch = CliLaunch();
            var policy = new ImagePolicy
            {
                MaxDuration = TimeSpan.FromSeconds(20),
                RectifySymbols = false,
            };

            PageImageResult? unavailable = PageWorker.Read(
                path, "page.png", policy,
                launch: new PageWorker.WorkerLaunch(path + ".not-an-executable", []));
            Assert.Null(unavailable);

            // A missing file makes both children exit unsuccessfully before they can emit a
            // result. The xUnit host is the parent here: it must remain alive and usable. Native
            // termination follows the same parent path because both cases are a non-success exit.
            PageImageResult? failed = PageWorker.Read(
                path + ".missing", "missing.png", policy, launch: launch);
            Assert.Null(failed);

            var progress = new List<PageImageProgress>();
            PageImageResult? recovered = PageWorker.Read(
                path, "page.png", policy, progress.Add, launch);

            Assert.NotNull(recovered);
            Assert.Null(recovered.Failure);
            Assert.Single(recovered.Symbols);
            Assert.NotEmpty(progress);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The real CLI apphost, which is what production uses as its worker. The CLI is self-contained,
    /// so its build output sits under a runtime-identifier directory; the search covers the case
    /// where the SDK picked a different identifier than this test host reports.
    /// </summary>
    private static PageWorker.WorkerLaunch CliLaunch()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string outputRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "BinaryPaper.Recovery.Cli", "bin", configuration, "net10.0"));
        string appHost = OperatingSystem.IsWindows() ? "binarypaper.exe" : "binarypaper";

        string preferred = Path.Combine(outputRoot, RuntimeInformation.RuntimeIdentifier, appHost);
        string? executable = File.Exists(preferred)
            ? preferred
            : Directory.Exists(outputRoot)
                ? Directory.EnumerateFiles(outputRoot, appHost, SearchOption.AllDirectories).FirstOrDefault()
                : null;

        Assert.True(executable is not null, $"worker executable was not built under {outputRoot}");
        return new PageWorker.WorkerLaunch(executable!, []);
    }

    private static byte[] MakePayload(int seed, int length)
    {
        var random = new Random(seed);
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
