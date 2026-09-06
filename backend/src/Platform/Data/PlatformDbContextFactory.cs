using Lagerkraft.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lagerkraft.Platform.Data;

public sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=platform;Username=lagerkraft;Password=lagerkraft")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new PlatformDbContext(options, new SystemClock());
    }
}
