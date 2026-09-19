using System.Net;
using System.Net.Http.Json;
using Lagerkraft.WmsCore.Api.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace Lagerkraft.WmsCore.Tests;

/// <summary>
/// CommandRegistry is a singleton; validators must be resolvable from the root provider
/// (Aspire / first request), not only from a request scope.
/// </summary>
public sealed class CommandRegistryTests : IAsyncLifetime
{
    private readonly WmsCoreApiFactory _factory = new() { ValidateScopes = true };

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public void CommandRegistry_RootProvider_ResolvesHandlers()
    {
        var registry = _factory.Services.GetRequiredService<CommandRegistry>();
        registry.TryGetHandler("Probe", out _).ShouldBeTrue();
        registry.TryGetHandler("CreateTask", out _).ShouldBeTrue();
        registry.TryGetHandler("ClaimTask", out _).ShouldBeTrue();
        registry.TryGetHandler("CreateLocationBatch", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Compat_ReturnsMinCommandVersions()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Internal-Token", WmsCoreApiFactory.InternalToken);
        var response = await client.GetAsync("/internal/compat");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompatDto>();
        body.ShouldNotBeNull();
        body.MinCommandVersions.ShouldContainKey("CreateTask");
        body.MinCommandVersions["CreateTask"].ShouldBe(1);
    }

    private sealed record CompatDto(
        [property: System.Text.Json.Serialization.JsonPropertyName("min_command_versions")]
        Dictionary<string, int> MinCommandVersions,
        [property: System.Text.Json.Serialization.JsonPropertyName("latest_app_version")]
        string LatestAppVersion);
}
