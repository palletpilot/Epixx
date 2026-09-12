using System.Security.Cryptography;
using Lagerkraft.SyncGateway.Clients;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text.Json;
using Lagerkraft.Shared.Auth;

namespace Lagerkraft.SyncGateway.Tests;

public sealed class SyncGatewayApiFactory : WebApplicationFactory<Program>
{
    public FakePlatformSyncClient Platform { get; } = new();
    public FakeWmsCoreSyncClient Wms { get; } = new();
    public RSA Rsa { get; } = RSA.Create(2048);
    public string PrivateKeyPem { get; }

    public SyncGatewayApiFactory()
    {
        PrivateKeyPem = Rsa.ExportRSAPrivateKeyPem();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Services:Platform", "http://platform.test");
        builder.UseSetting("Services:WmsCore", "http://wms.test");
        builder.UseSetting("Internal:Token", "test-internal-token");
        builder.UseSetting("Jwt:PrivateKeyPem", PrivateKeyPem);
        builder.UseSetting("Compat:MinAppVersion", "0.9.0");
        builder.UseSetting("Compat:LatestAppVersion", "1.0.0");
        builder.ConfigureTestServices(services =>
        {
            foreach (var d in services.Where(x => x.ServiceType == typeof(IPlatformSyncClient)).ToList())
            {
                services.Remove(d);
            }

            foreach (var d in services.Where(x => x.ServiceType == typeof(IWmsCoreSyncClient)).ToList())
            {
                services.Remove(d);
            }

            // Remove typed HttpClient registrations that would conflict
            foreach (var d in services.Where(x =>
                         x.ServiceType == typeof(HttpPlatformSyncClient)
                         || x.ServiceType == typeof(HttpWmsCoreSyncClient)).ToList())
            {
                services.Remove(d);
            }

            services.AddSingleton<IPlatformSyncClient>(Platform);
            services.AddSingleton<IWmsCoreSyncClient>(Wms);
        });
    }

    public string IssueToken(Guid userId, Guid tenantId, int sessionVersion, Guid? deviceId, string? role = null)
    {
        var claims = new List<Claim>
        {
            Str("sub", userId.ToString()),
            Str(LagerkraftClaims.TenantId, tenantId.ToString()),
            Str(LagerkraftClaims.SessionVersion, sessionVersion.ToString()),
            Str(LagerkraftClaims.AuthMethod, "pwd")
        };
        if (deviceId is { } d)
        {
            claims.Add(Str(LagerkraftClaims.DeviceId, d.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            var ra = JsonSerializer.Serialize(new[] { new Dictionary<string, object?> { ["r"] = role, ["w"] = "*" } });
            claims.Add(Str(LagerkraftClaims.RoleAssignments, ra));
        }

        var key = new RsaSecurityKey(Rsa) { KeyId = "sync-test-1" };
        var creds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "lagerkraft",
            Audience = "lagerkraft",
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(15),
            SigningCredentials = creds
        });
    }

    private static Claim Str(string type, string value) =>
        new(type, JsonSerializer.Serialize(value), JsonClaimValueTypes.Json);
}
