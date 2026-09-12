using Lagerkraft.Platform.Auth;
using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Email;
using Lagerkraft.Platform.Internal;
using Lagerkraft.Platform.Provisioning;
using Lagerkraft.Platform.Auth.Oidc;
using Lagerkraft.Platform.Devices;
using Lagerkraft.Platform.Signup;
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

var emailProvider = builder.Configuration["Email:Provider"] ?? "mailpit";
if (string.Equals(emailProvider, "mailjet", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IEmailSender, MailjetSender>();
}
else
{
    builder.Services.AddSingleton<IEmailSender, MailpitSmtpSender>();
}

var dbCreator = builder.Configuration["Provisioning:DatabaseCreator"] ?? "postgres";
if (string.Equals(dbCreator, "upcloud", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ITenantDatabaseCreator, UpCloudApiCreator>();
}
else
{
    builder.Services.AddSingleton<ITenantDatabaseCreator, PostgresCreateDatabase>();
}

var wmsBase = builder.Configuration["Services:WmsCore"];
if (string.IsNullOrWhiteSpace(wmsBase))
{
    builder.Services.AddSingleton<IWmsCoreMigrateClient, NoopWmsCoreMigrateClient>();
}
else
{
    builder.Services.AddHttpClient<IWmsCoreMigrateClient, WmsCoreMigrateClient>(client =>
    {
        client.BaseAddress = new Uri(wmsBase.TrimEnd('/') + "/");
    });
}

builder.Services.AddHostedService<ProvisioningJob>();
builder.Services.AddHostedService<PurgeUnverifiedJob>();
builder.Services.AddHostedService<ClientSecretExpiryJob>();
builder.Services.AddSingleton<DynamicOidcHandler>();
builder.Services.AddSingleton<OidcStateStore>();
builder.Services.AddScoped<OidcProvisioner>();

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
app.MapSignupApi();
app.MapOidcApi();
app.MapDevicesApi();

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