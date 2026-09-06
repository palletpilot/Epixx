using Lagerkraft.Platform.Data;
using Lagerkraft.Shared;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Platform.Tests;

public sealed class PlatformDbContextTests
{
    [Fact]
    public void Model_HasNoPendingChanges()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new PlatformDbContext(options, new SystemClock());
        db.Database.HasPendingModelChanges().ShouldBeFalse();
    }
}
