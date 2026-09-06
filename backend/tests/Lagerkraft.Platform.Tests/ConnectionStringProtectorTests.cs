using Lagerkraft.Platform.Tenancy;

namespace Lagerkraft.Platform.Tests;

public sealed class ConnectionStringProtectorTests
{
    [Fact]
    public void Decrypt_WhatEncryptProduced_RoundTrips()
    {
        var kek = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var protector = new ConnectionStringProtector(kek);
        const string connection = "Host=db;Database=tenant_x;Username=u;Password=p";

        var (wrappedDek, ciphertext) = protector.Encrypt(connection);

        protector.Decrypt(wrappedDek, ciphertext).ShouldBe(connection);
        wrappedDek.ShouldNotBe(ciphertext);
    }

    [Fact]
    public void Decrypt_WrongKek_Throws()
    {
        var protector = new ConnectionStringProtector(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var (wrappedDek, ciphertext) = protector.Encrypt("Host=db");
        var other = new ConnectionStringProtector(Enumerable.Range(2, 32).Select(i => (byte)i).ToArray());

        Should.Throw<System.Security.Cryptography.CryptographicException>(
            () => other.Decrypt(wrappedDek, ciphertext));
    }
}
