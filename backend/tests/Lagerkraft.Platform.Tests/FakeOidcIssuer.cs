using System.Net;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Lagerkraft.Platform.Tests;

/// <summary>Minimal OIDC issuer for integration tests (~hand-rolled JWT + discovery).</summary>
public sealed class FakeOidcIssuer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, bool> _codes = new();
    private readonly string _prefix;
    private Task? _loop;

    public FakeOidcIssuer()
    {
        _prefix = $"http://127.0.0.1:{GetFreePort()}/";
        _listener.Prefixes.Add(_prefix);
        SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = "test" }, SecurityAlgorithms.RsaSha256);
        Issuer = _prefix.TrimEnd('/');
    }

    public string Issuer { get; }
    public string ClientId { get; } = "test-client";
    public string ClientSecret { get; } = "test-secret";
    public SigningCredentials SigningCredentials { get; }

    public bool Started { get; private set; }

    public async Task StartAsync()
    {
        if (Started)
        {
            return;
        }

        _listener.Start();
        Started = true;
        _loop = Task.Run(ListenAsync);
        await Task.Delay(50);
    }

    public string IssueIdToken(
        string subject,
        string email,
        bool emailVerified,
        IEnumerable<string>? roles = null,
        IEnumerable<string>? groups = null,
        bool groupsOverflow = false,
        TimeSpan? lifetime = null)
    {
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("email", email),
            new("email_verified", emailVerified ? "true" : "false")
        };
        if (roles is not null)
        {
            foreach (var r in roles)
            {
                claims.Add(new Claim("roles", r));
            }
        }

        if (groupsOverflow)
        {
            claims.Add(new Claim("_claim_names", """{"groups":"src1"}"""));
        }
        else if (groups is not null)
        {
            foreach (var g in groups)
            {
                claims.Add(new Claim("groups", g));
            }
        }

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(5)),
            SigningCredentials = SigningCredentials
        });
        return handler.WriteToken(token);
    }

    private async Task ListenAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            var path = ctx.Request.Url!.AbsolutePath;
            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                await WriteJson(ctx, $$"""
                {
                  "issuer": "{{Issuer}}",
                  "authorization_endpoint": "{{Issuer}}/authorize",
                  "token_endpoint": "{{Issuer}}/token",
                  "jwks_uri": "{{Issuer}}/jwks",
                  "response_types_supported": ["code"],
                  "subject_types_supported": ["public"],
                  "id_token_signing_alg_values_supported": ["RS256"]
                }
                """);
            }
            else if (path.EndsWith("/jwks", StringComparison.Ordinal))
            {
                var p = _rsa.ExportParameters(false);
                await WriteJson(ctx, $$"""
                {
                  "keys": [{
                    "kty": "RSA",
                    "use": "sig",
                    "kid": "test",
                    "alg": "RS256",
                    "n": "{{Base64UrlEncoder.Encode(p.Modulus!)}}",
                    "e": "{{Base64UrlEncoder.Encode(p.Exponent!)}}"
                  }]
                }
                """);
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }
    }

    private static async Task WriteJson(HttpListenerContext ctx, string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        if (_loop is not null)
        {
            try { await _loop; } catch { /* ignore */ }
        }

        _rsa.Dispose();
    }
}