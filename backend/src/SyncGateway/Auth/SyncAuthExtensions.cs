using System.Security.Cryptography;
using System.Text.Json;
using Lagerkraft.Shared.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Lagerkraft.SyncGateway.Auth;

public static class SyncAuthExtensions
{
    public const string Issuer = "lagerkraft";
    public const string Audience = "lagerkraft";

    public static IServiceCollection AddSyncGatewayAuth(this IServiceCollection services, IConfiguration config)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IHttpClientFactory>((options, httpFactory) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Issuer,
                    ValidAudience = Audience,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.Zero,
                    NameClaimType = "sub"
                };

                var pem = config["Jwt:PrivateKeyPem"];
                if (!string.IsNullOrWhiteSpace(pem))
                {
                    var rsa = RSA.Create();
                    rsa.ImportFromPem(pem);
                    options.TokenValidationParameters.IssuerSigningKey =
                        new RsaSecurityKey(rsa) { KeyId = "sync-test-1" };
                }
                else
                {
                    var jwksUrl = config["Jwt:JwksUrl"]
                        ?? throw new InvalidOperationException("Jwt:JwksUrl or Jwt:PrivateKeyPem is required");
                    options.TokenValidationParameters.IssuerSigningKeyResolver =
                        (_, _, _, _) => LoadJwks(httpFactory, jwksUrl);
                }
            });
        services.AddAuthorization();
        return services;
    }

    private static IEnumerable<SecurityKey> LoadJwks(IHttpClientFactory httpFactory, string jwksUrl)
    {
        // Must use IHttpClientFactory so Aspire https+http://{service}/ URLs resolve.
        var http = httpFactory.CreateClient();
        var json = http.GetStringAsync(jwksUrl).GetAwaiter().GetResult();
        var jwks = new JsonWebKeySet(json);
        return jwks.GetSigningKeys();
    }

    public static bool TryReadSyncPrincipal(
        System.Security.Claims.ClaimsPrincipal principal,
        out SyncPrincipal sync)
    {
        sync = default!;
        var sub = Unwrap(principal.FindFirst("sub")?.Value);
        var tid = Unwrap(principal.FindFirst(LagerkraftClaims.TenantId)?.Value);
        var svRaw = Unwrap(principal.FindFirst(LagerkraftClaims.SessionVersion)?.Value);
        var devRaw = Unwrap(principal.FindFirst(LagerkraftClaims.DeviceId)?.Value);
        if (!Guid.TryParse(sub, out var userId) || !Guid.TryParse(tid, out var tenantId))
        {
            return false;
        }

        if (!int.TryParse(svRaw, out var sv))
        {
            return false;
        }

        Guid? deviceId = null;
        if (!string.IsNullOrWhiteSpace(devRaw) && Guid.TryParse(devRaw, out var d))
        {
            deviceId = d;
        }

        sync = new SyncPrincipal(userId, tenantId, sv, deviceId);
        return true;
    }

    private static string? Unwrap(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(value);
            }
            catch (JsonException)
            {
                return value.Trim('"');
            }
        }

        return value;
    }
}

public sealed record SyncPrincipal(Guid UserId, Guid TenantId, int SessionVersion, Guid? DeviceId);
