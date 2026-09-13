using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lagerkraft.SyncGateway.Clients;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;

namespace Lagerkraft.SyncGateway.Tests;

/// <summary>
/// AppHost sets Jwt:JwksUrl to https+http://platform/... which a raw HttpClient cannot fetch.
/// Validation must go through IHttpClientFactory (service discovery).
/// </summary>
[Trait("Category", "Integration")]
public sealed class JwksDiscoveryTests : IAsyncLifetime
{
    private readonly SyncGatewayApiFactory _factory = new();
    private WebApplication? _jwks;
    private Guid _tenantId;
    private Guid _userId;
    private Guid _deviceId;

    public async Task InitializeAsync()
    {
        _tenantId = Guid.CreateVersion7();
        _userId = Guid.CreateVersion7();
        _deviceId = Guid.CreateVersion7();
        _factory.Platform.Devices[_deviceId] = new InternalDevice(
            _deviceId, _tenantId, "floor-1", [_tenantId], null, 0);
        _factory.Platform.Entitlement = new TenantEntitlement(_tenantId, "Trialing", false, "pro", 1000);
        _factory.Platform.CurrentSessionVersion = 1;

        var parameters = _factory.Rsa.ExportParameters(false);
        var jwksJson = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    kid = "sync-test-1",
                    alg = "RS256",
                    n = Base64UrlEncoder.Encode(parameters.Modulus!),
                    e = Base64UrlEncoder.Encode(parameters.Exponent!)
                }
            }
        });

        var jwksBuilder = WebApplication.CreateBuilder();
        jwksBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        _jwks = jwksBuilder.Build();
        _jwks.MapGet("/.well-known/jwks.json", () => Results.Content(jwksJson, "application/json"));
        await _jwks.StartAsync();
        var origin = _jwks.Urls.First();

        _factory.JwksUrl = "https+http://jwks/.well-known/jwks.json";
        _factory.JwksServiceOrigin = origin;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        if (_jwks is not null)
        {
            await _jwks.DisposeAsync();
        }
    }

    [Fact]
    public async Task Commands_HttpsPlusHttpJwksUrl_AcceptsSignedToken()
    {
        using var client = _factory.CreateClient();
        var token = _factory.IssueToken(_userId, _tenantId, 1, _deviceId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/sync/commands", new
        {
            now = DateTimeOffset.UtcNow,
            commands = new[]
            {
                new
                {
                    id = Guid.CreateVersion7(),
                    type = "Probe",
                    v = 1,
                    payload = JsonSerializer.SerializeToElement(new { probe_id = Guid.CreateVersion7(), message = "x" }),
                    occurred_at = DateTimeOffset.UtcNow,
                    device_id = _deviceId,
                    user_id = _userId
                }
            }
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _factory.Wms.CommandCalls.ShouldBe(1);
    }
}
