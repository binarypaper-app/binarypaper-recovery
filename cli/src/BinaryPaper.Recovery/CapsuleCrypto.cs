// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace BinaryPaper.Recovery;

/// <summary>
/// Authentication: AES-256-GCM for encrypted capsules, SHA-256 for plaintext ones.
/// SPEC.md sections 10.1 to 10.4.
/// </summary>
/// <remarks>
/// This is the security boundary of the whole pipeline. Nothing downstream — LZMA, ZIP, the
/// manifest, entry names — sees a byte that has not passed through here first.
/// </remarks>
public static class CapsuleCrypto
{
    /// <summary>
    /// Normalizes a password to NFC and encodes it as UTF-8, as the specification requires.
    /// </summary>
    /// <remarks>
    /// Observable, not cosmetic: a password typed in a decomposed form must open a capsule created
    /// with the same password typed in a composed form. Skipping this silently fails to open
    /// capsules that a conforming reader opens, and the user has no way to tell why.
    /// </remarks>
    public static byte[] EncodePassword(string password) =>
        Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));

    /// <summary>Derives the AES key with Argon2id, using the parameters carried in the preamble.</summary>
    public static byte[] DeriveKey(string password, CapsulePreamble preamble)
    {
        var generator = new Argon2BytesGenerator();
        var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13)
            .WithSalt(preamble.Salt)
            .WithMemoryAsKB((int)preamble.KdfMemoryKib)
            .WithIterations((int)preamble.KdfIterations)
            .WithParallelism((int)preamble.KdfParallelism)
            .Build();

        generator.Init(parameters);
        byte[] key = new byte[preamble.KdfOutputLength];
        generator.GenerateBytes(EncodePassword(password), key);
        return key;
    }

    /// <summary>
    /// Authenticates the body and returns the protected bytes.
    /// </summary>
    /// <remarks>
    /// A wrong password and a tampered ciphertext both surface as <c>auth.failed</c>. They are
    /// cryptographically indistinguishable, and claiming to tell them apart would be a false
    /// diagnostic offered at exactly the moment a user is most likely to act on it.
    /// </remarks>
    public static byte[] Authenticate(byte[] body, CapsulePreamble preamble, string? password)
    {
        if (!preamble.IsEncrypted)
        {
            byte[] actual = SHA256.HashData(body);
            if (!CryptographicOperations.FixedTimeEquals(actual, preamble.PlaintextDigest))
            {
                throw new RecoveryException(RecoveryErrorCategory.AuthDigestMismatch, RecoveryStage.Authentication,
                    "The plaintext body does not match the SHA-256 digest recorded in the capsule preamble. "
                    + "The recovered bytes are not the bytes that were written.");
            }

            return body;
        }

        if (password is null)
        {
            throw new RecoveryException(RecoveryErrorCategory.AuthFailed, RecoveryStage.Authentication,
                "This capsule is encrypted and no password was supplied.");
        }

        byte[] key = DeriveKey(password, preamble);
        try
        {
            int tagLength = RecoveryProfile.AesGcmTagLength;
            int ciphertextLength = body.Length - tagLength;
            ReadOnlySpan<byte> ciphertext = body.AsSpan(0, ciphertextLength);
            ReadOnlySpan<byte> tag = body.AsSpan(ciphertextLength);

            byte[] plaintext = new byte[ciphertextLength];
            using var aes = new AesGcm(key, tagLength);

            // Associated data is empty by design: every field influencing recovery already feeds
            // either key derivation or the reconstructed ciphertext, both of which the tag covers.
            aes.Decrypt(preamble.Nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (CryptographicException)
        {
            throw new RecoveryException(RecoveryErrorCategory.AuthFailed, RecoveryStage.Authentication,
                "Authentication failed. The password is wrong, or the capsule has been modified. "
                + "These are indistinguishable to the cryptography and this tool will not guess between them.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
