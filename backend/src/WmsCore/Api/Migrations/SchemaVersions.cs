using Lagerkraft.WmsCore.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Migrations;

public static class SchemaVersions
{
    public static string Required(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetService<IDbContextFactory<TenantDbContext>>();
        if (factory is not null)
        {
            using var db = factory.CreateDbContext();
            return db.Database.GetMigrations().LastOrDefault() ?? "none";
        }

        // Fall back: migrations embedded on TenantDbContext model
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var probe = new TenantDbContext(options);
        return probe.Database.GetMigrations().LastOrDefault() ?? "none";
    }

    public static string RequiredFromAssembly()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var probe = new TenantDbContext(options);
        return probe.Database.GetMigrations().LastOrDefault() ?? "none";
    }
}
