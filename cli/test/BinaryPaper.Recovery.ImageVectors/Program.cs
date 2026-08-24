// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BinaryPaper.Recovery;
using BinaryPaper.Recovery.Images;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using BinaryPaper.Recovery.ImageVectors;

// Generates the image conformance vectors from the raw-frame vectors that already exist.
//
// Deriving them keeps the image layer honest: the QR codes carry exactly the frame bytes the
// capsule suite already pins, so an image vector that recovers proves the whole path from pixels to
// restored content, and a difference can only come from the image layer itself.
//
// Everything is deterministic. The QR matrices are a pure function of the frame bytes, and the
// rendering, rotation and JPEG steps use fixed parameters, so regenerating reproduces the same
// bytes on any machine.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: image-vectors <vectors-root> <output-suite-root>");
    return 2;
}

string vectorsRoot = args[0];
string outputRoot = args[1];

int written = 0;

written += WriteSinglePage(
    "image-single-page-png",
    "One QR code in a clean PNG: the simplest possible image recovery.",
    Path.Combine(vectorsRoot, "1.0", "positive", "plaintext-text-note"),
    ImageFormat.Png, rotationQuarterTurns: 0);

written += WriteSinglePage(
    "image-multi-code-png",
    "Every code of a capsule on one page image, the way a real printed sheet looks.",
    Path.Combine(vectorsRoot, "1.0", "positive", "plaintext-text-note"),
    ImageFormat.Png, rotationQuarterTurns: 0, allOnOnePage: true);

written += WriteSinglePage(
    "image-rotated-png",
    "A page scanned upside down. Orientation must not matter.",
    Path.Combine(vectorsRoot, "1.0", "positive", "plaintext-text-note"),
    ImageFormat.Png, rotationQuarterTurns: 2, allOnOnePage: true);

written += WriteSinglePage(
    "image-binary-payload-png",
    "Ciphertext frames containing 0x00 and invalid UTF-8. Pins the binary path: a reader that "
        + "round-trips a payload through text fails here and nowhere else.",
    Path.Combine(vectorsRoot, "1.0", "positive", "encrypted-file-tree"),
    ImageFormat.Png, rotationQuarterTurns: 0, allOnOnePage: true);

written += WriteSinglePage(
    "image-jpeg",
    "The same page as JPEG. Lossy compression must not defeat the error correction.",
    Path.Combine(vectorsRoot, "1.0", "positive", "plaintext-text-note"),
    ImageFormat.Jpeg, rotationQuarterTurns: 0, allOnOnePage: true);

// ---------------------------------------------------------------- image negatives
//
// These pin behaviour a positive vector cannot: what happens when a page is partly unreadable, and
// what happens when someone photographs two different backups together. Both are ordinary user
// situations, not exotic attacks, which is exactly why they need pinning.

written += WriteDamagedPage(
    "image-damaged-code-recoverable",
    "A page whose first code is scribbled out. The remaining codes plus a repair symbol must still "
        + "recover the capsule - this is what the redundancy is for.",
    Path.Combine(vectorsRoot, "1.0", "positive", "plaintext-text-note"));

written += WriteMixedCapsules(
    "image-mixed-capsules",
    "One page carrying codes from two different capsules. A reader must report both rather than "
        + "silently merging them or picking the larger one.",
    Path.Combine(vectorsRoot, "1.0", "positive", "plaintext-text-note"),
    Path.Combine(vectorsRoot, "1.0", "positive", "encrypted-file-tree"));

MergeIntoAggregateManifest(outputRoot);

Console.WriteLine($"Wrote {written} image vector(s) under {Path.Combine(outputRoot, "1.0", "images")}.");
return 0;

// The aggregate manifest hashes every vector manifest in the suite, image vectors included, so a
// hand-edited expectation is caught rather than believed. Entries are merged rather than rewritten
// because the capsule vectors in this file are produced by a different generator; regenerating the
// capsule suite and then the image suite is the documented order.
void MergeIntoAggregateManifest(string suiteRoot)
{
    string aggregatePath = Path.Combine(suiteRoot, "MANIFEST.json");
    JsonObject aggregate = File.Exists(aggregatePath)
        ? JsonNode.Parse(File.ReadAllText(aggregatePath))!.AsObject()
        : new JsonObject
        {
            ["schemaVersion"] = 1,
            ["protocol"] = new JsonObject { ["formatMajor"] = 1, ["formatMinor"] = 0 },
            ["schema"] = "schema/vector-manifest.schema.json",
            ["vectorCount"] = 0,
            ["vectors"] = new JsonArray()
        };

    var entries = new List<JsonNode>();
    foreach (JsonNode? existing in aggregate["vectors"]!.AsArray())
    {
        // Drop any previous image entries; they are being rewritten below.
        string? existingCategory = (existing as JsonObject)?["category"]?.GetValue<string>();
        if (existing is JsonObject obj && existingCategory != "image" && existingCategory != "image-negative")
        {
            entries.Add(JsonNode.Parse(obj.ToJsonString())!);
        }
    }

    string imagesRoot = Path.Combine(suiteRoot, "1.0", "images");
    foreach (string manifestPath in Directory.GetFiles(imagesRoot, "manifest.json", SearchOption.AllDirectories)
                 .OrderBy(x => x, StringComparer.Ordinal))
    {
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        string relative = Path.GetRelativePath(suiteRoot, manifestPath).Replace('\\', '/');

        entries.Add(new JsonObject
        {
            ["id"] = manifest["id"]!.GetValue<string>(),
            ["category"] = JsonNode.Parse(File.ReadAllText(manifestPath))!["category"]!.GetValue<string>(),
            ["manifest"] = relative,
            ["sha256"] = Sha256Hex(File.ReadAllBytes(manifestPath))
        });
    }

    var ordered = entries
        .OrderBy(e => e!["manifest"]!.GetValue<string>(), StringComparer.Ordinal)
        .Select(e => JsonNode.Parse(e!.ToJsonString())!)
        .ToArray();

    aggregate["vectorCount"] = ordered.Length;
    aggregate["vectors"] = new JsonArray(ordered);

    File.WriteAllText(
        aggregatePath,
        aggregate.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n",
        new UTF8Encoding(false));

    Console.WriteLine($"  aggregate manifest now lists {ordered.Length} vector(s).");
}

int WriteSinglePage(
    string id,
    string title,
    string sourceVectorDirectory,
    ImageFormat format,
    int rotationQuarterTurns,
    bool allOnOnePage = false)
{
    string[] framePaths = Directory.GetFiles(sourceVectorDirectory, "*.bpq");
    Array.Sort(framePaths, StringComparer.Ordinal);

    if (framePaths.Length == 0)
    {
        throw new InvalidOperationException($"No frames under {sourceVectorDirectory}.");
    }

    List<byte[]> frames = allOnOnePage
        ? [.. framePaths.Select(File.ReadAllBytes)]
        : [File.ReadAllBytes(framePaths[0])];

    byte[] page = PageRenderer.Render(frames, rotationQuarterTurns, format);

    string directory = Path.Combine(outputRoot, "1.0", "images", id);
    Directory.CreateDirectory(directory);

    string imageName = format == ImageFormat.Png ? "page-0.png" : "page-0.jpg";
    File.WriteAllBytes(Path.Combine(directory, imageName), page);

    // Assert the vector actually works before it is written as a fixture. A generated image that
    // nobody decoded is a guess, and a suite full of guesses is worse than no suite.
    var reader = new PageImageReader();
    PageImageResult result = reader.Read(imageName, page);

    if (result.Symbols.Count != frames.Count)
    {
        throw new InvalidOperationException(
            $"{id}: rendered {frames.Count} code(s) but decoded {result.Symbols.Count} ({result.Failure}).");
    }

    // Byte-exact, not merely "decoded something". This is the check that catches a decoder
    // recoding a binary payload through text.
    var decoded = result.Symbols.Select(s => s.Payload).ToList();
    foreach (byte[] frame in frames)
    {
        if (!decoded.Any(d => d.AsSpan().SequenceEqual(frame)))
        {
            throw new InvalidOperationException(
                $"{id}: a rendered frame did not decode back to its exact bytes.");
        }
    }

    var manifest = new JsonObject
    {
        ["schemaVersion"] = 1,
        ["protocol"] = new JsonObject { ["formatMajor"] = 1, ["formatMinor"] = 0 },
        ["id"] = id,
        ["category"] = "image",
        ["title"] = title,
        ["operation"] = "decode-image",
        ["inputs"] = new JsonArray(new JsonObject
        {
            ["path"] = imageName,
            ["length"] = page.Length,
            ["sha256"] = Sha256Hex(page),
            ["role"] = "page-image"
        }),
        ["expected"] = new JsonObject
        {
            ["result"] = "success",
            ["frameCount"] = frames.Count,
            ["frames"] = new JsonArray([.. frames.Select(f => (JsonNode)new JsonObject
            {
                ["length"] = f.Length,
                ["sha256"] = Sha256Hex(f)
            })])
        },
        // Image decoding is a capability, not a baseline. A reader that only takes raw frames -
        // the capsule tooling on the creator side, for instance - skips these rather than being
        // counted as failing something it was never meant to do.
        ["requires"] = new JsonArray("image-decoding"),
        ["provenance"] = new JsonObject
        {
            ["generator"] = "BinaryPaper.Recovery.ImageVectors",
            ["recipe"] = $"QR matrices rendered from the frames of '{Path.GetFileName(sourceVectorDirectory)}' "
                + $"at {PageRenderer.ModuleSize}px per module, {PageRenderer.Quiet}-module quiet zone, "
                + $"{rotationQuarterTurns * 90} degree rotation, format {format}"
                + (format == ImageFormat.Jpeg ? $", JPEG quality {PageRenderer.JpegQuality}" : string.Empty),
            ["derivedFrom"] = Path.GetFileName(sourceVectorDirectory)
        }
    };

    File.WriteAllText(
        Path.Combine(directory, "manifest.json"),
        manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n",
        new UTF8Encoding(false));

    Console.WriteLine($"  {id}: {frames.Count} code(s), {page.Length} bytes, decoded byte-exact");
    return 1;
}


/// <summary>
/// Renders a page and then destroys one code, so a reader must recover from what is left.
/// </summary>
/// <remarks>
/// The damage is a solid block over the first symbol's area rather than random noise. Noise
/// sometimes still decodes, which would make the vector pass or fail depending on the decoder's
/// error correction rather than on the rule under test.
/// </remarks>
int WriteDamagedPage(string id, string title, string sourceVectorDirectory)
{
    string[] framePaths = Directory.GetFiles(sourceVectorDirectory, "*.bpq");
    Array.Sort(framePaths, StringComparer.Ordinal);
    List<byte[]> frames = [.. framePaths.Select(File.ReadAllBytes)];

    byte[] page = PageRenderer.Render(frames, rotationQuarterTurns: 0, ImageFormat.Png, obliterateFirstCode: true);

    string directory = Path.Combine(outputRoot, "1.0", "images", id);
    Directory.CreateDirectory(directory);
    File.WriteAllBytes(Path.Combine(directory, "page-0.png"), page);

    var reader = new PageImageReader();
    PageImageResult result = reader.Read("page-0.png", page);

    // The vector is only meaningful if the damage actually removed a code and left the rest usable.
    if (result.Symbols.Count != frames.Count - 1)
    {
        throw new InvalidOperationException(
            $"{id}: expected {frames.Count - 1} readable code(s) after damage, got {result.Symbols.Count}.");
    }

    // And only meaningful if what survives is still enough to recover.
    var surviving = result.Symbols.Select(sym => ("page", sym.Payload)).ToList();
    FrameIngestResult ingest = CapsuleRecovery.Ingest(surviving);
    ScanSession session = ingest.Sessions.Single(x => x.Reference is not null);
    RestoredContent restored = CapsuleRecovery.Recover(session, null, ResourcePolicy.Default);

    WriteImageManifest(directory, id, "image-negative", title, "page-0.png", page,
        new JsonObject
        {
            ["result"] = "success",
            ["frameCount"] = result.Symbols.Count,
            ["note"] = "one code is unreadable by design; the survivors must still recover the capsule",
            ["payloadKind"] = restored.Kind switch
            {
                PayloadKind.TextNote => "text-note",
                PayloadKind.SingleFile => "single-file",
                _ => "file-tree"
            }
        },
        $"frames of '{Path.GetFileName(sourceVectorDirectory)}' rendered to one page, then the first "
        + "code's area filled solid black",
        Path.GetFileName(sourceVectorDirectory));

    Console.WriteLine($"  {id}: {result.Symbols.Count} of {frames.Count} code(s) readable, still recovers");
    return 1;
}

/// <summary>Renders codes from two different capsules onto one page.</summary>
int WriteMixedCapsules(string id, string title, string firstVectorDirectory, string secondVectorDirectory)
{
    byte[] first = File.ReadAllBytes(Directory.GetFiles(firstVectorDirectory, "*.bpq").OrderBy(x => x, StringComparer.Ordinal).First());
    byte[] second = File.ReadAllBytes(Directory.GetFiles(secondVectorDirectory, "*.bpq").OrderBy(x => x, StringComparer.Ordinal).First());

    byte[] page = PageRenderer.Render([first, second], rotationQuarterTurns: 0, ImageFormat.Png);

    string directory = Path.Combine(outputRoot, "1.0", "images", id);
    Directory.CreateDirectory(directory);
    File.WriteAllBytes(Path.Combine(directory, "page-0.png"), page);

    var reader = new PageImageReader();
    PageImageResult result = reader.Read("page-0.png", page);

    if (result.Symbols.Count != 2)
    {
        throw new InvalidOperationException($"{id}: expected 2 codes, decoded {result.Symbols.Count}.");
    }

    FrameIngestResult ingest = CapsuleRecovery.Ingest(
        [.. result.Symbols.Select(sym => ("page", sym.Payload))]);

    int capsuleCount = ingest.Sessions.Count(x => x.Reference is not null);
    if (capsuleCount != 2)
    {
        throw new InvalidOperationException(
            $"{id}: the two codes must group into 2 capsules, got {capsuleCount}. "
            + "If this is 1, sessions are merging capsules that must stay separate.");
    }

    WriteImageManifest(directory, id, "image-negative", title, "page-0.png", page,
        new JsonObject
        {
            ["result"] = "session.multiple-capsules",
            ["stage"] = "session",
            ["frameCount"] = 2,
            ["capsuleCount"] = 2,
            ["note"] = "a reader must report both capsules rather than merging them or choosing one"
        },
        "one frame from each of two different capsules rendered onto a single page",
        $"{Path.GetFileName(firstVectorDirectory)} + {Path.GetFileName(secondVectorDirectory)}");

    Console.WriteLine($"  {id}: 2 code(s) from {capsuleCount} distinct capsules");
    return 1;
}

void WriteImageManifest(
    string directory, string id, string category, string title,
    string imageName, byte[] page, JsonObject expected, string recipe, string derivedFrom)
{
    var manifest = new JsonObject
    {
        ["schemaVersion"] = 1,
        ["protocol"] = new JsonObject { ["formatMajor"] = 1, ["formatMinor"] = 0 },
        ["id"] = id,
        ["category"] = category,
        ["title"] = title,
        ["operation"] = "decode-image",
        ["inputs"] = new JsonArray(new JsonObject
        {
            ["path"] = imageName,
            ["length"] = page.Length,
            ["sha256"] = Sha256Hex(page),
            ["role"] = "page-image"
        }),
        ["requires"] = new JsonArray("image-decoding"),
        ["expected"] = expected,
        ["provenance"] = new JsonObject
        {
            ["generator"] = "BinaryPaper.Recovery.ImageVectors",
            ["recipe"] = recipe,
            ["derivedFrom"] = derivedFrom
        }
    };

    File.WriteAllText(
        Path.Combine(directory, "manifest.json"),
        manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n",
        new UTF8Encoding(false));
}

static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

internal enum ImageFormat
{
    Png,
    Jpeg
}

/// <summary>
/// Renders QR matrices onto a page and encodes it as PNG or JPEG.
/// </summary>
/// <remarks>
/// The encoders are written here rather than pulled from a library because the kit's runtime
/// dependency is decode-only, and adding an imaging stack purely to generate test fixtures would
/// enlarge the audit surface of the shipped tool for no runtime benefit.
///
/// PNG is written with stored (uncompressed) deflate blocks: trivially correct, and size does not
/// matter for a fixture. JPEG is produced by a minimal baseline encoder, which is enough to prove
/// that lossy input still decodes.
/// </remarks>
internal static class PageRenderer
{
    public const int ModuleSize = 6;
    public const int Quiet = 4;
    public const int JpegQuality = 85;
    private const int Columns = 3;
    private const int Gap = 24;

    public static byte[] Render(
        IReadOnlyList<byte[]> frames, int rotationQuarterTurns, ImageFormat format,
        bool obliterateFirstCode = false)
    {
        List<bool[,]> matrices = [.. frames.Select(EncodeMatrix)];

        int cell = matrices.Max(m => m.GetLength(0)) * ModuleSize + (2 * Quiet * ModuleSize);
        int columns = Math.Min(Columns, matrices.Count);
        int rows = (matrices.Count + columns - 1) / columns;

        int width = (columns * cell) + ((columns + 1) * Gap);
        int height = (rows * cell) + ((rows + 1) * Gap);

        byte[] page = new byte[width * height];
        Array.Fill(page, (byte)255);

        for (int i = 0; i < matrices.Count; i++)
        {
            int column = i % columns;
            int row = i / columns;
            int originX = Gap + (column * (cell + Gap));
            int originY = Gap + (row * (cell + Gap));
            Blit(page, width, matrices[i], originX, originY);
        }

        if (obliterateFirstCode)
        {
            // A solid block over the whole first cell, not noise. Noise sometimes still decodes,
            // which would make the vector's outcome depend on the decoder's error correction rather
            // than on the rule under test.
            for (int y = Gap; y < Gap + cell && y < height; y++)
            {
                for (int x = Gap; x < Gap + cell && x < width; x++)
                {
                    page[(y * width) + x] = 0;
                }
            }
        }

        for (int turn = 0; turn < (rotationQuarterTurns % 4 + 4) % 4; turn++)
        {
            (page, width, height) = RotateQuarterTurn(page, width, height);
        }

        return format == ImageFormat.Png
            ? PngWriter.WriteGrey(page, width, height)
            : JpegWriter.WriteGrey(page, width, height, JpegQuality);
    }

    private static bool[,] EncodeMatrix(byte[] frame)
    {
        // ISO-8859-1 is the round-trip-safe single-byte mapping ZXing uses to accept arbitrary bytes
        // as a Byte Mode payload. Every byte 0x00-0xFF maps to exactly one character and back, so
        // the encoded symbol carries the frame verbatim.
        var latin1 = Encoding.GetEncoding(28591);
        string payload = latin1.GetString(frame);

        var writer = new QRCodeWriter();
        var hints = new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.CHARACTER_SET] = "ISO-8859-1",
            [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
            [EncodeHintType.MARGIN] = 0
        };

        BitMatrix matrix = writer.encode(payload, BarcodeFormat.QR_CODE, 0, 0, hints);
        var result = new bool[matrix.Width, matrix.Height];
        for (int y = 0; y < matrix.Height; y++)
        {
            for (int x = 0; x < matrix.Width; x++)
            {
                result[x, y] = matrix[x, y];
            }
        }

        return result;
    }

    private static void Blit(byte[] page, int pageWidth, bool[,] matrix, int originX, int originY)
    {
        int modules = matrix.GetLength(0);
        for (int my = 0; my < modules; my++)
        {
            for (int mx = 0; mx < modules; mx++)
            {
                if (!matrix[mx, my])
                {
                    continue;
                }

                int x0 = originX + ((mx + Quiet) * ModuleSize);
                int y0 = originY + ((my + Quiet) * ModuleSize);
                for (int dy = 0; dy < ModuleSize; dy++)
                {
                    int offset = ((y0 + dy) * pageWidth) + x0;
                    for (int dx = 0; dx < ModuleSize; dx++)
                    {
                        page[offset + dx] = 0;
                    }
                }
            }
        }
    }

    private static (byte[] Pixels, int Width, int Height) RotateQuarterTurn(byte[] pixels, int width, int height)
    {
        byte[] rotated = new byte[pixels.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                rotated[(x * height) + (height - 1 - y)] = pixels[(y * width) + x];
            }
        }

        return (rotated, height, width);
    }
}
