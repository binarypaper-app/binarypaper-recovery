// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BinaryPaper.Recovery;
using Erasure16;
using LdpcStaircase;
using SharpCompress.Compressors.LZMA;

// Test fixtures only: accepts a reviewed recipe, never user files or user passwords.
// Encodes the public wire layouts directly. No application code or private feed is needed.
if (args.Length != 2) { Console.Error.WriteLine("usage: corpus <recipes.json> <new-output-directory>"); return 2; }
string root = Path.GetFullPath(args[1]);
if (Directory.Exists(root)) throw new IOException("Choose a new output directory");
Directory.CreateDirectory(root);
JsonArray recipes = JsonNode.Parse(File.ReadAllText(args[0]))!["recipes"]!.AsArray();
var cases = new JsonArray();
const string TestPassword = "public-boundary-test-only-é";
foreach (JsonNode? node in recipes)
{
    JsonObject recipe = node!.AsObject();
    string id = recipe["id"]!.GetValue<string>();
    if (id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new InvalidDataException("Invalid recipe id");
    int k = recipe["sourceSymbolCount"]!.GetValue<int>(), r = recipe["repairSymbolCount"]!.GetValue<int>();
    int s = recipe["symbolLen"]!.GetValue<int>(), algorithm = recipe["erasureAlg"]!.GetValue<int>();
    int missing = recipe["missingSourceCount"]!.GetValue<int>(), entryCount = recipe["entryCount"]!.GetValue<int>();
    bool encrypted = recipe["encrypted"]!.GetValue<bool>(), compressed = recipe["compressed"]!.GetValue<bool>();
    string directory = Path.Combine(root, id);
    Directory.CreateDirectory(directory);

    var entries = new List<(string Path, byte[] Bytes)>();
    byte[] manifestName = Encoding.UTF8.GetBytes("Synthetic boundary fixture");
    byte[] manifest = new byte[8 + manifestName.Length];
    "BPMF"u8.CopyTo(manifest); manifest[4] = 1;
    BinaryPrimitives.WriteUInt16BigEndian(manifest.AsSpan(6), (ushort)manifestName.Length);
    manifestName.CopyTo(manifest, 8);
    entries.Add((PayloadPackage.ManifestEntryName, manifest));
    for (int i = 0; i < entryCount; i++)
        entries.Add((entryCount == 1 ? "boundary.bin" : $"paths/utf8-é/entry-{i:D5}.bin", []));
    int payloadBytes = recipe["contentBytes"]!.GetValue<int>();
    if (payloadBytes == 0)
        payloadBytes = checked(k * s - (encrypted ? 75 : 63) - ZipStore(entries).Length);
    if (payloadBytes < entryCount) throw new InvalidDataException("Recipe cannot hold its package metadata");
    var outputs = new JsonArray();
    for (int i = 1; i < entries.Count; i++)
    {
        int size = payloadBytes / entryCount + (i <= payloadBytes % entryCount ? 1 : 0);
        byte[] bytes = compressed ? new byte[size] : Bytes(size, id + "/" + i);
        entries[i] = (entries[i].Path, bytes);
        outputs.Add(new JsonObject { ["path"] = entries[i].Path, ["length"] = size, ["sha256"] = Hash(bytes) });
    }
    byte[] package = ZipStore(entries);
    byte[] body = compressed ? Compress(package) : package;
    byte[] salt = encrypted ? Bytes(16, id + "/salt") : [];
    byte[] nonce = encrypted ? Bytes(12, id + "/nonce") : [];
    byte[] preamble = new byte[encrypted ? 59 : 63];
    "BPCP"u8.CopyTo(preamble);
    preamble[4] = preamble[5] = encrypted ? (byte)1 : (byte)0;
    preamble[6] = compressed ? (byte)1 : (byte)0;
    if (encrypted)
    {
        BinaryPrimitives.WriteUInt32BigEndian(preamble.AsSpan(7), RecoveryProfile.KdfMemoryKib);
        BinaryPrimitives.WriteUInt32BigEndian(preamble.AsSpan(11), RecoveryProfile.KdfIterations);
        BinaryPrimitives.WriteUInt32BigEndian(preamble.AsSpan(15), RecoveryProfile.KdfParallelism);
        BinaryPrimitives.WriteUInt16BigEndian(preamble.AsSpan(19), RecoveryProfile.KdfOutputLength);
        preamble[21] = 16; preamble[22] = 12;
        salt.CopyTo(preamble, 31); nonce.CopyTo(preamble, 47);
    }
    else SHA256.HashData(body).CopyTo(preamble, 31);
    BinaryPrimitives.WriteUInt64BigEndian(preamble.AsSpan(23), (ulong)(body.Length + (encrypted ? 16 : 0)));
    if (encrypted)
    {
        byte[] key = CapsuleCrypto.DeriveKey(TestPassword, CapsulePreamble.Decode(preamble));
        byte[] cipher = new byte[body.Length + 16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, body, cipher.AsSpan(0, body.Length), cipher.AsSpan(body.Length));
        CryptographicOperations.ZeroMemory(key);
        body = cipher;
    }
    byte[] stored = [.. preamble, .. body];
    // Compression changes the actual writer count; do not keep invented all-zero source frames.
    if (compressed)
    {
        k = (stored.Length + s - 1) / s;
        r = (k + 1) / 2;
        missing = Math.Min(missing, r);
    }
    if (stored.Length > (long)k * s || stored.Length <= (long)(k - 1) * s)
        throw new InvalidDataException($"{id}: stored length does not produce the declared source count");
    byte[] capsuleId = Bytes(4, id + "/capsule");
    var symbols = new byte[k + r][];
    byte[] padded = new byte[k * s]; stored.CopyTo(padded, 0);
    for (int i = 0; i < k; i++) symbols[i] = padded.AsSpan(i * s, s).ToArray();
    for (int i = k; i < symbols.Length; i++) symbols[i] = new byte[s];
    uint seed = ScanSession.DeriveLdpcSeed(capsuleId);
    if (algorithm == 2) LdpcStaircaseCodec.Create(k, r, (int)seed).Encode(symbols);
    else if (r > 0) ReedSolomon16.Create(k, r).Encode(symbols);

    string decodeStage = missing == 0 ? "None" : "ReedSolomon";
    int residualUnknowns = 0;
    if (algorithm == 2)
    {
        // Decode two-byte zero symbols to verify the exact erasure pattern and record whether
        // it reaches the residual solve. This tests rank without copying the large payload.
        byte[][] probe = new byte[k + r][];
        bool[] present = new bool[k + r];
        for (int i = missing; i < probe.Length; i++) { probe[i] = new byte[2]; present[i] = true; }
        DecodeResult result = LdpcStaircaseCodec.Create(k, r, (int)seed).Decode(probe, present,
            DecodeOptions.Default.WithMaxResidualUnknowns(RecoveryProfile.LdpcMaxResidualUnknowns));
        if (!result.IsComplete) throw new InvalidDataException($"{id}: selected loss pattern is not recoverable");
        decodeStage = result.Stage.ToString(); residualUnknowns = result.ResidualUnknownCount;
    }
    string container = Path.Combine(directory, "frames.bin");
    using (var stream = File.Create(container))
    {
        WriteU32(stream, (uint)(symbols.Length - missing));
        for (int i = missing; i < symbols.Length; i++)
        {
            byte[] frame = new byte[23 + s];
            "BPQR"u8.CopyTo(frame); frame[4] = 1; frame[6] = (byte)algorithm;
            capsuleId.CopyTo(frame, 7);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(11), (ushort)k);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(13), (ushort)r);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(15), (ushort)s);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(17), (ushort)i);
            symbols[i].CopyTo(frame, 19);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(frame.Length - 4), Crc32C.Compute(frame.AsSpan(0, frame.Length - 4)));
            RecoveryProfile.EnsureInProfile(CapsuleFrame.Decode(frame));
            WriteU32(stream, (uint)frame.Length); stream.Write(frame);
        }
    }
    long predicted = algorithm == 2 ? RecoveryProfile.PeakLdpcBytes(k, r, s) : RecoveryProfile.PeakReedSolomonBytes(k, r, s);
    cases.Add(new JsonObject {
        ["id"] = id, ["sourceSymbolCount"] = k, ["repairSymbolCount"] = r, ["symbolLen"] = s,
        ["erasureAlg"] = algorithm, ["missingSourceCount"] = missing, ["frameCount"] = k + r - missing,
        ["storedPayloadBytes"] = stored.Length, ["packageBytes"] = package.Length,
        ["decodeStage"] = decodeStage, ["residualUnknownCount"] = residualUnknowns,
        ["predictedPeakRestoreBytes"] = predicted, ["framesContainerSha256"] = Hash(File.ReadAllBytes(container)),
        ["outputs"] = outputs, ["password"] = encrypted ? new JsonObject { ["kind"] = "public-test-value", ["value"] = TestPassword } : null
    });
    Console.WriteLine($"{id}: K={k} R={r} S={s}, missing={missing}, stage={decodeStage}, residual={residualUnknowns}");
}
File.WriteAllText(Path.Combine(root, "corpus.json"), cases.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
    .Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));
return 0;

static void WriteU32(Stream stream, uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); stream.Write(b); }
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static byte[] Bytes(int length, string domain)
{
    byte[] result = new byte[length];
    byte[] seed = Encoding.UTF8.GetBytes("recovery-boundary-v2/" + domain);
    byte[] input = new byte[seed.Length + 8]; seed.CopyTo(input, 0);
    for (int offset = 0; offset < length; offset += 32)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(seed.Length), (ulong)(offset / 32));
        byte[] block = SHA256.HashData(input);
        block.AsSpan(0, Math.Min(32, length - offset)).CopyTo(result.AsSpan(offset));
    }
    return result;
}
static byte[] Compress(byte[] package)
{
    using var raw = new MemoryStream();
    byte[] properties;
    using (var encoder = LzmaStream.Create(new LzmaEncoderProperties(eos: false, dictionary: 1 << 20), false, raw))
    { properties = encoder.Properties; encoder.Write(package); }
    byte[] result = new byte[13 + raw.Length]; properties.CopyTo(result, 0);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(5), (ulong)package.Length);
    raw.ToArray().CopyTo(result, 13); return result;
}
// Minimal ZIP-Store fixture writer. Fixed DOS epoch, UTF-8 names, no platform-specific attributes.
static byte[] ZipStore(List<(string Path, byte[] Bytes)> entries)
{
    using var result = new MemoryStream(); using var writer = new BinaryWriter(result, Encoding.UTF8, true);
    var central = new List<(byte[] Name, uint Crc, uint Size, uint Offset)>();
    foreach (var entry in entries)
    {
        byte[] name = Encoding.UTF8.GetBytes(entry.Path); uint crc = Crc32(entry.Bytes);
        uint size = (uint)entry.Bytes.Length, offset = (uint)result.Position;
        writer.Write(0x04034b50u); writer.Write((ushort)20); writer.Write((ushort)0x800); writer.Write((ushort)0);
        writer.Write((ushort)0); writer.Write((ushort)33); writer.Write(crc); writer.Write(size); writer.Write(size);
        writer.Write((ushort)name.Length); writer.Write((ushort)0); writer.Write(name); writer.Write(entry.Bytes);
        central.Add((name, crc, size, offset));
    }
    uint centralOffset = (uint)result.Position;
    foreach (var e in central)
    {
        writer.Write(0x02014b50u); writer.Write((ushort)20); writer.Write((ushort)20); writer.Write((ushort)0x800);
        writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)33); writer.Write(e.Crc);
        writer.Write(e.Size); writer.Write(e.Size); writer.Write((ushort)e.Name.Length);
        writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write(0u); writer.Write(e.Offset); writer.Write(e.Name);
    }
    uint centralSize = (uint)result.Position - centralOffset;
    writer.Write(0x06054b50u); writer.Write((ushort)0); writer.Write((ushort)0);
    writer.Write((ushort)entries.Count); writer.Write((ushort)entries.Count);
    writer.Write(centralSize); writer.Write(centralOffset); writer.Write((ushort)0);
    return result.ToArray();
}
static uint Crc32(byte[] bytes)
{
    uint crc = uint.MaxValue;
    foreach (byte b in bytes) { crc ^= b; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
    return ~crc;
}
