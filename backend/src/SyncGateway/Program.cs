using System.Threading.RateLimiting;
using Lagerkraft.SyncGateway.Auth;
using Lagerkraft.SyncGateway.Clients;
using Lagerkraft.SyncGateway.Sync;
using Lagerkraft.SyncGateway.Sync.Beacon;
using Lagerkraft.SyncGateway.Sync.Changes;
using Lagerkraft.SyncGateway.Sync.Commands;
using Lagerkraft.SyncGateway.Sync.Compat;
using Lagerkraft.SyncGateway.Sync.Snapshot;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSyncGatewayAuth(builder.Configuration);
builder.Services.AddSingleton<DeviceBatchGate>();
builder.Services.AddOpenApi("sync-gateway");

builder.Services.AddHttpClient<IPlatformSyncClient, HttpPlatformSyncClient>((sp, client) =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["Services:Platform"];
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        throw new InvalidOperationException("Services:Platform is required");
    }

    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    var token = sp.GetRequiredService<IConfiguration>()["Internal:Token"] ?? "";
    client.DefaultRequestHeaders.TryAddWithoutValidation("Lagerkraft-Internal-Token", token);
});

builder.Services.AddHttpClient<IWmsCoreSyncClient, HttpWmsCoreSyncClient>((sp, client) =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["Services:WmsCore"];
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        throw new InvalidOperationException("Services:WmsCore is required");
    }

    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    var token = sp.GetRequiredService<IConfiguration>()["Internal:Token"] ?? "";
    client.DefaultRequestHeaders.TryAddWithoutValidation("Lagerkraft-Internal-Token", token);
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("tenant", httpContext =>
    {
        var tid = httpContext.User.FindFirst("tid")?.Value ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(tid, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "1";
        await context.HttpContext.Response.WriteAsync("rate_limited", ct);
    };
});

var app = builder.Build();
app.MapDefaultEndpoints("sync-gateway");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

var sync = app.MapGroup("/sync").RequireRateLimiting("tenant");
sync.MapSyncCommands();
sync.MapSyncChanges();
sync.MapSyncSnapshot();
sync.MapSyncCompat();
sync.MapSyncBeacon();

app.Run();

public partial class Program;
