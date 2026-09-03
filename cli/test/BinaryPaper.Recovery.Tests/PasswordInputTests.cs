// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using BinaryPaper.Recovery.Cli;
using Xunit;

namespace BinaryPaper.Recovery.Tests;

public sealed class PasswordInputTests
{
    [Theory]
    [InlineData("café", "café")]
    [InlineData("cafe\u0301", "cafe\u0301")]
    [InlineData("\uFEFFпароль-é", "пароль-é")]
    public void StdinPreservesUnicodeBeforeKeyNormalization(string input, string expected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input + "\r\nignored"));
        Assert.Equal(expected, PasswordInput.ReadUtf8(stream));
        Assert.Equal(CapsuleCrypto.EncodePassword(expected), CapsuleCrypto.EncodePassword(PasswordInput.ReadUtf8(
            new MemoryStream(Encoding.UTF8.GetBytes(input)))!));
    }

    [Fact]
    public void InvalidUtf8IsRefusedWithoutEchoingPasswordBytes()
    {
        using var stream = new MemoryStream([0x66, 0x80, 0x6f, 0x0a]);
        var error = Assert.Throws<UsageException>(() => PasswordInput.ReadUtf8(stream));
        Assert.Equal("Password input must be UTF-8.", error.Message);
    }
}
