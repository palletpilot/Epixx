using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Migrations;
using Lagerkraft.WmsCore.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Tenancy;

public sealed class SchemaVersionMiddleware(RequestDelegate next, ILogger<SchemaVersionMiddleware> log)
{
    private static readonly HashSet<string> WriteMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete
    };

    public async Task InvokeAsync(
        HttpContext context,
        ITenantConnectionCache connections)
    {
        if (!WriteMethods.Contains(context.Request.Method)
            || context.Request.Path.StartsWithSegments("/health")
            || context.Request.Path.StartsWithSegments("/internal/tenants") && context.Request.Path.Value?.EndsWith("/migrate", StringComparison.Ordinal) == true)
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("Lagerkraft-Tenant-Id", out var tenantHeader)
            || !Guid.TryParse(tenantHeader, out var tenantId))
        {
            await next(context);
            return;
        }

        var connection = await connections.GetConnectionStringAsync(tenantId, context.RequestAborted);
        if (connection is null)
        {
            await next(context);
            return;
        }

        var required = SchemaVersions.RequiredFromAssembly();
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new TenantDbContext(options);
        var applied = (await db.Database.GetAppliedMigrationsAsync(context.RequestAborted)).LastOrDefault() ?? "none";
        if (!string.Equals(applied, required, StringComparison.Ordinal))
        {
            log.LogWarning(
                "Tenant {TenantId} schema {Applied} != required {Required}; write blocked",
                tenantId,
                applied,
                required);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers["Lagerkraft-Tenant-Maintenance"] = "schema_mismatch";
            return;
        }

        await next(context);
    }
}
