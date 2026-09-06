using System.Security.Claims;
using Lagerkraft.Shared.Auth;
using Lagerkraft.Shared.Tenancy;
using Microsoft.AspNetCore.Http;

namespace Lagerkraft.Shared.Tests;

public sealed class TenantResolutionTests
{
    [Fact]
    public async Task Invoke_HostSlugWithoutTid_PopulatesTenantContext()
    {
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString("acme.lagerkraft.se");
        var ran = false;
        var middleware = new TenantResolutionMiddleware(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(http);

        ran.ShouldBeTrue();
        var tenant = http.Features.Get<TenantContext>();
        tenant.ShouldNotBeNull();
        tenant.Slug.ShouldNotBeNull();
        tenant.Slug.Value.ShouldBe("acme");
        tenant.TenantId.ShouldBe(Guid.Empty);
    }

    [Fact]
    public async Task Invoke_JwtTidPresent_WinsOverHost()
    {
        var id = Guid.CreateVersion7();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(LagerkraftClaims.TenantId, id.ToString())
            ], "test"))
        };
        http.Request.Host = new HostString("other.lagerkraft.se");
        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(http);

        var tenant = http.Features.Get<TenantContext>();
        tenant.ShouldNotBeNull();
        tenant.TenantId.ShouldBe(id);
    }
}
