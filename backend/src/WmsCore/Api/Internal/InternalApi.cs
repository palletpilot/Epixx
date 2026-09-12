using Lagerkraft.WmsCore.Api.Tenancy;

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
        IPlatformTenantClient platform,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Lagerkraft.WmsCore.Api.Internal.Migrate");
        try
        {
            var connection = await platform.GetConnectionAsync(id, ct);
            if (connection is null)
            {
                return Results.NotFound();
            }

            // Early C1 stub: no-op DDL so Platform B3 can call this endpoint.
            await platform.PutMigrationStatusAsync(id, "stub", "up_to_date", null, ct);
            return Results.Ok();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Migrate failed for tenant {TenantId}", id);
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}
