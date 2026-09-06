using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Internal;
using Lagerkraft.Platform.Tenancy;
using Lagerkraft.Shared;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ConnectionStringProtector>();
builder.Services.AddScoped<TenantCatalog>();
builder.Services.AddDbContext<PlatformDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("platform"))
        .UseSnakeCaseNamingConvention();
});

var app = builder.Build();
app.MapDefaultEndpoints("platform");
app.MapInternalApi();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    await db.Database.MigrateAsync();
    await PlatformSeeder.SeedAsync(db);
}

app.Run();

public partial class Program;
