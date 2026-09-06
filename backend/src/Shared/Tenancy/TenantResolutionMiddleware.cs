using Lagerkraft.Shared.Auth;
using Microsoft.AspNetCore.Http;

namespace Lagerkraft.Shared.Tenancy;

public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    private const string HostSuffix = ".lagerkraft.se";

    public async Task InvokeAsync(HttpContext http)
    {
        var context = Resolve(http);
        if (context is not null)
        {
            http.Features.Set(context);
        }

        await next(http);
    }

    private static TenantContext? Resolve(HttpContext http)
    {
        var tid = http.User.FindFirst(LagerkraftClaims.TenantId)?.Value;
        if (Guid.TryParse(tid, out var tenantId))
        {
            return new TenantContext { TenantId = tenantId };
        }

        var host = http.Request.Host.Host;
        if (host.EndsWith(HostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var left = host[..^HostSuffix.Length];
            if (!left.Contains('.') && Slug.TryCreate(left, out var slug))
            {
                return new TenantContext { Slug = slug };
            }
        }

        return null;
    }
}
