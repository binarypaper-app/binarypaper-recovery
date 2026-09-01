// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BinaryPaper.Recovery.Images;
using StbImageSharp;
using ZXingCpp;

// How many codes does each way of reading a page actually recover?
//
// The boundary benchmarks next door ask whether the largest capsule the product writes can be
// recovered without exhausting a machine. This asks something the conformance vectors cannot: on
// real photographs, how much does each stage of the reading pipeline contribute, and what does it
// cost? The vectors are clean synthetic renderings by design, so they answer neither.
//
// It exists because that question was once answered by measuring only the endpoints, which
// credited the expensive stage with a gain that belonged to the cheap one. Anything that claims a
// stage earns its place should be re-runnable rather than remembered.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: page-yield <image-directory> [--json <results.json>]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Point it at a directory of page photographs. Corpora are not committed:");
    Console.Error.WriteLine("  they are large, and a photograph of a screen tends to carry more than the page.");
    return 2;
}

string corpus = args[0];
string? jsonPath = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--json")
    {
        jsonPath = args[i + 1];
    }
}

string[] files = Directory.GetFiles(corpus)
    .Where(f => PageImageReader.LooksLikeImage(File.ReadAllBytes(f).AsSpan(0, Math.Min(16, (int)new FileInfo(f).Length))))
    .OrderBy(f => f, StringComparer.Ordinal)
    .ToArray();

if (files.Length == 0)
{
    Console.Error.WriteLine($"no page images found in {corpus}");
    return 2;
}

var strategies = new[] { "plain", "plain-both-binarizers", "whole-page-sweep", "full-pipeline" };
var totals = strategies.ToDictionary(s => s, _ => new HashSet<string>(StringComparer.Ordinal));
var seconds = strategies.ToDictionary(s => s, _ => 0.0);
var perImage = new List<object>();

Console.WriteLine($"{files.Length} page image(s) in {corpus}");
Console.WriteLine();
Console.WriteLine($"{"image",-28} {"plain",6} {"both",6} {"sweep",6} {"full",6}   {"sweep s",8} {"full s",8}");

foreach (string file in files)
{
    byte[] bytes = File.ReadAllBytes(file);
    string name = Path.GetFileName(file);
    ImageResult image = ImageResult.FromMemory(bytes, ColorComponents.Grey);

    var counts = new Dictionary<string, int>();

    // "Plain" is the picture handed to the decoder exactly as it arrived. The options mirror the
    // reader's own so the only difference measured is the preprocessing, not the configuration.
    var plain = Decode(image, [Binarizer.LocalAverage]);
    var both = Decode(image, [Binarizer.LocalAverage, Binarizer.GlobalHistogram]);

    var sweepClock = Stopwatch.StartNew();
    PageImageResult sweep = new PageImageReader(new ImagePolicy { RectifySymbols = false }).Read(name, bytes);
    sweepClock.Stop();

    var fullClock = Stopwatch.StartNew();
    PageImageResult full = new PageImageReader().Read(name, bytes);
    fullClock.Stop();

    var results = new (string Strategy, IEnumerable<string> Hashes)[]
    {
        ("plain", plain),
        ("plain-both-binarizers", both),
        ("whole-page-sweep", sweep.Symbols.Select(s => Hash(s.Payload))),
        ("full-pipeline", full.Symbols.Select(s => Hash(s.Payload))),
    };

    foreach ((string strategy, IEnumerable<string> hashes) in results)
    {
        var distinct = hashes.ToHashSet(StringComparer.Ordinal);
        counts[strategy] = distinct.Count;
        totals[strategy].UnionWith(distinct);
    }

    seconds["whole-page-sweep"] += sweepClock.Elapsed.TotalSeconds;
    seconds["full-pipeline"] += fullClock.Elapsed.TotalSeconds;

    Console.WriteLine($"{name,-28} {counts["plain"],6} {counts["plain-both-binarizers"],6} "
        + $"{counts["whole-page-sweep"],6} {counts["full-pipeline"],6}   "
        + $"{sweepClock.Elapsed.TotalSeconds,8:F1} {fullClock.Elapsed.TotalSeconds,8:F1}");

    perImage.Add(new
    {
        image = name,
        width = image.Width,
        height = image.Height,
        codes = counts,
        sweepSeconds = Math.Round(sweepClock.Elapsed.TotalSeconds, 2),
        fullSeconds = Math.Round(fullClock.Elapsed.TotalSeconds, 2),
        fullTruncated = full.Truncated,
    });
}

Console.WriteLine();
Console.WriteLine("distinct codes across the corpus:");
foreach (string strategy in strategies)
{
    string cost = seconds[strategy] > 0 ? $"{seconds[strategy],8:F1}s" : "";
    Console.WriteLine($"  {strategy,-24} {totals[strategy].Count,4} {cost}");
}

Console.WriteLine();
Console.WriteLine("The gap between sweep and full is what rectify-and-retry contributed. It rescues");
Console.WriteLine("symbols the detector located but could not read, so it earns its cost only where");
Console.WriteLine("the sweep fell short - which is why the reader escalates to it rather than always");
Console.WriteLine("running it.");

if (jsonPath is not null)
{
    var document = new
    {
        tool = "BinaryPaper.Recovery.PageYield",
        measuredUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        machine = new
        {
            os = Environment.OSVersion.VersionString,
            architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        },
        note = "Counts are distinct QR payloads. Corpus contents are not recorded here: page "
            + "photographs are the user's own and are kept outside version control.",
        corpusImageCount = files.Length,
        distinct = strategies.ToDictionary(s => s, s => totals[s].Count),
        seconds = new
        {
            wholePageSweep = Math.Round(seconds["whole-page-sweep"], 2),
            fullPipeline = Math.Round(seconds["full-pipeline"], 2),
        },
        images = perImage,
    };

    File.WriteAllText(jsonPath, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine();
    Console.WriteLine($"Wrote {jsonPath}");
}

return 0;

static HashSet<string> Decode(ImageResult image, Binarizer[] binarizers)
{
    var found = new HashSet<string>(StringComparer.Ordinal);

    foreach (Binarizer binarizer in binarizers)
    {
        using var options = new ReaderOptions
        {
            Formats = BarcodeFormat.QRCode,
            TryHarder = true,
            TryRotate = true,
            TryDownscale = true,
            TryInvert = false,
            MaxNumberOfSymbols = 255,
            ReturnErrors = false,
            Binarizer = binarizer,
        };

        NativeBarcodeReader.Read(image.Data, image.Width, image.Height, ImageFormat.Lum, options, barcode =>
        {
            if (barcode.IsValid && barcode.Bytes is { Length: > 0 })
            {
                found.Add(Hash(barcode.Bytes));
            }
        });
    }

    return found;
}

static string Hash(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload));
