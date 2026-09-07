using Lagerkraft.Platform.Auth;
using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Internal;
using Lagerkraft.Platform.Tenancy;
using Lagerkraft.Shared;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ConnectionStringProtector>();
builder.Services.AddScoped<TenantCatalog>();
builder.Services.AddSingleton<SigningKey>();
builder.Services.AddSingleton<JwtIssuer>();
builder.Services.AddScoped<RefreshTokenStore>();
builder.Services.AddScoped<SessionVersionBump>();
builder.Services.AddSingleton<IPlatformEventPublisher>(sp =>
{
    var url = sp.GetRequiredService<IConfiguration>().GetConnectionString("nats")
        ?? sp.GetRequiredService<IConfiguration>()["NATS_URL"];
    if (string.IsNullOrWhiteSpace(url))
    {
        return new NoopPlatformEventPublisher();
    }

    return new NatsPlatformEventPublisher(
        new NatsConnection(new NatsOpts { Url = url }),
        sp.GetRequiredService<IClock>());
});
builder.Services.AddDbContext<PlatformDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("platform"))
        .UseSnakeCaseNamingConvention();
});
builder.Services
    .AddIdentityCore<AppUser>(options => options.User.RequireUniqueEmail = true)
    .AddEntityFrameworkStores<PlatformDbContext>()
    .AddDefaultTokenProviders();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtIssuer>((options, jwt) =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = jwt.ValidationParameters();
    });
builder.Services.AddAuthorization();
builder.Services.AddOpenApi("platform");

var app = builder.Build();
app.MapDefaultEndpoints("platform");
app.MapInternalApi();
app.MapAuthApi();

if (!IsOpenApiDocumentGeneration())
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    await db.Database.MigrateAsync();
    await PlatformSeeder.SeedAsync(db);
}

app.Run();

static bool IsOpenApiDocumentGeneration()
{
    var entry = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "";
    return entry.Contains("GetDocument", StringComparison.OrdinalIgnoreCase);
}

public partial class Program;
