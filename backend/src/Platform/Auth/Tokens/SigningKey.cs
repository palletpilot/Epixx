using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Lagerkraft.Platform.Auth;

public sealed class SigningKey
{
    public const string KeyId = "lagerkraft-platform-1";

    public SigningKey(IConfiguration configuration)
    {
        var pem = configuration["Jwt:PrivateKeyPem"];
        Rsa = RSA.Create();
        if (string.IsNullOrWhiteSpace(pem))
        {
            Rsa.KeySize = 2048;
        }
        else
        {
            Rsa.ImportFromPem(pem);
        }

        Credentials = new SigningCredentials(new RsaSecurityKey(Rsa) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256);
    }

    public RSA Rsa { get; }
    public SigningCredentials Credentials { get; }
}
