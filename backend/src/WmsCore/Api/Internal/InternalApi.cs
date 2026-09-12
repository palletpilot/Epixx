using Lagerkraft.WmsCore.Api.Tenancy;
using Lagerkraft.WmsCore.Api.Migrations;

namespace Lagerkraft.WmsCore.Api.Internal;

public static class InternalApi
{
    public static WebApplication MapInternalApi(this WebApplication app)
    {
        var group = app.MapGroup("/internal").AddEndpointFilter<InternalTokenFilter>();
        group.MapPost("/tenants/{id:guid}/migrate", Migrate);
        return app;
    }

    private static async Task<IResult> Migrate(
        Guid id,
        ITenantMigrator migrator,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Lagerkraft.WmsCore.Api.Internal.Migrate");
        try
        {
            await migrator.MigrateTenantAsync(id, ct);
            return Results.Ok();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("connection not found", StringComparison.OrdinalIgnoreCase))
        {
            return Results.NotFound();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Migrate failed for tenant {TenantId}", id);
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}
