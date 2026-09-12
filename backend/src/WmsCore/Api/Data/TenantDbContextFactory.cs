using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lagerkraft.WmsCore.Api.Data;

public sealed class TenantDbContextFactory : IDesignTimeDbContextFactory<TenantDbContext>
{
    public TenantDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=tenant_migrate;Username=lagerkraft;Password=lagerkraft")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new TenantDbContext(options);
    }
}
