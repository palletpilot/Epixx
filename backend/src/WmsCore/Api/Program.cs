using System.CommandLine;
using System.Text.Json.Serialization;
using FluentValidation;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Catalog;
using Lagerkraft.WmsCore.Api.Commands;
using Lagerkraft.WmsCore.Api.Commands.Probe;
using Lagerkraft.WmsCore.Api.Commands.Layout;
using Lagerkraft.WmsCore.Api.Commands.Inventory;
using Lagerkraft.WmsCore.Api.Commands.Tasks;
using Lagerkraft.WmsCore.Api.Internal;
using Lagerkraft.WmsCore.Api.Jobs;
using Lagerkraft.WmsCore.Api.Migrations;
using Lagerkraft.WmsCore.Api.Relay;
using Lagerkraft.WmsCore.Api.Tenancy;
using Lagerkraft.WmsCore.Api.Warehouses;
using NATS.Client.Core;

if (args is ["migrate", ..] or ["relay", ..] or ["replay", ..] or ["verify", ..])
{
    return await RunCliAsync(args);
}

var builder = WebApplication.CreateBuilder(args);
ConfigureServices(builder);
var app = builder.Build();
ConfigureApp(app);
app.Run();
return 0;

static async Task<int> RunCliAsync(string[] args)
{
    var root = new RootCommand("Lagerkraft wms-core");

    var tenantOption = new Option<Guid?>("--tenant") { Description = "Tenant id" };
    var allOption = new Option<bool>("--all");
    var migrateCommand = new Command("migrate", "Apply tenant DB migrations");
    migrateCommand.Options.Add(tenantOption);
    migrateCommand.Options.Add(allOption);
    migrateCommand.SetAction(async (parseResult, ct) =>
    {
        var tenant = parseResult.GetValue(tenantOption);
        var all = parseResult.GetValue(allOption);
        var hostBuilder = WebApplication.CreateBuilder([]);
        ConfigureServices(hostBuilder);
        await using var host = hostBuilder.Build();
        var migrator = host.Services.GetRequiredService<ITenantMigrator>();
        if (all)
        {
            throw new NotSupportedException("migrate --all needs a platform tenant list endpoint; use --tenant <id>");
        }

        if (tenant is null)
        {
            throw new ArgumentException("--tenant <guid> is required");
        }

        await migrator.MigrateTenantAsync(tenant.Value, ct);
        var relaySvc = host.Services.GetRequiredService<OutboxRelay>();
        var retentionSvc = host.Services.GetRequiredService<OutboxRetentionJob>();
        relaySvc.TrackTenant(tenant.Value);
        retentionSvc.TrackTenant(tenant.Value);
    });
    root.Subcommands.Add(migrateCommand);

    var relayCommand = new Command("relay", "Run outbox relay once for a tenant");
    relayCommand.Options.Add(tenantOption);
    relayCommand.SetAction(async (parseResult, ct) =>
    {
        var tenant = parseResult.GetValue(tenantOption)
            ?? throw new ArgumentException("--tenant <guid> is required");
        var hostBuilder = WebApplication.CreateBuilder([]);
        ConfigureServices(hostBuilder);
        await using var host = hostBuilder.Build();
        var relay = host.Services.GetRequiredService<OutboxRelay>();
        var n = await relay.RelayTenantAsync(tenant, ct);
        Console.WriteLine($"relayed {n}");
    });
    root.Subcommands.Add(relayCommand);

    var fromOption = new Option<Guid>("--from") { Required = true };
    var toOption = new Option<Guid>("--to") { Required = true };
    var replayCommand = new Command("replay", "Replay outbox range");
    replayCommand.Options.Add(tenantOption);
    replayCommand.Options.Add(fromOption);
    replayCommand.Options.Add(toOption);
    replayCommand.SetAction(async (parseResult, ct) =>
    {
        var tenant = parseResult.GetValue(tenantOption)
            ?? throw new ArgumentException("--tenant <guid> is required");
        var from = parseResult.GetValue(fromOption);
        var to = parseResult.GetValue(toOption);
        var hostBuilder = WebApplication.CreateBuilder([]);
        ConfigureServices(hostBuilder);
        await using var host = hostBuilder.Build();
        var replay = host.Services.GetRequiredService<OutboxReplay>();
        var n = await replay.ReplayAsync(tenant, from, to, ct);
        Console.WriteLine($"replayed {n}");
    });
    root.Subcommands.Add(replayCommand);

    root.Subcommands.Add(new Command("verify", "Post-migration verify"));
    return await root.Parse(args).InvokeAsync();
}

static void ConfigureServices(WebApplicationBuilder builder)
{
    builder.AddServiceDefaults();
    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
        o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
    builder.Services.AddSingleton<IClock, SystemClock>();
    builder.Services.AddHttpClient<IPlatformTenantClient, HttpPlatformTenantClient>((sp, client) =>
    {
        var baseUrl = sp.GetRequiredService<IConfiguration>()["Platform:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Platform:BaseUrl is required");
        }

        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        var token = sp.GetRequiredService<IConfiguration>()["Internal:Token"] ?? "";
        client.DefaultRequestHeaders.TryAddWithoutValidation("Lagerkraft-Internal-Token", token);
    });
    builder.Services.AddSingleton<ITenantConnectionCache, TenantConnectionCache>();
    builder.Services.AddSingleton<ITenantMigrator, TenantMigrator>();
    builder.Services.AddSingleton<IEntitlementCache, EntitlementCache>();
    builder.Services.AddSingleton<IStaleCommandHook, DefaultStaleCommandHook>();
    builder.Services.AddValidatorsFromAssemblyContaining<ProbeValidator>(ServiceLifetime.Singleton);
    builder.Services.AddSingleton<CommandRegistry>(sp =>
    {
        var registry = new CommandRegistry();
        registry.Register(ActivatorUtilities.CreateInstance<ProbeHandler>(sp));
        registry.Register(new ProbeV0ToV1Upcaster());
        registry.Register(ActivatorUtilities.CreateInstance<CreateTaskHandler>(sp));
        registry.Register(ActivatorUtilities.CreateInstance<ClaimTaskHandler>(sp));
        registry.Register(ActivatorUtilities.CreateInstance<ReleaseTaskHandler>(sp));
        registry.Register(ActivatorUtilities.CreateInstance<CompleteTaskHandler>(sp));
        registry.Register(ActivatorUtilities.CreateInstance<CreateLocationBatchHandler>(sp));
        registry.Register(ActivatorUtilities.CreateInstance<SetLocationDimensionsHandler>(sp));
        registry.Register(ActivatorUtilities.CreateInstance<ReceiveHandlingUnitHandler>(sp));
        return registry;
    });
    builder.Services.AddSingleton<CommandDispatcher>();
    builder.Services.AddSingleton<AssignmentSweepJob>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AssignmentSweepJob>());

    var natsUrl = builder.Configuration.GetConnectionString("nats")
        ?? builder.Configuration["NATS_URL"];
    if (!string.IsNullOrWhiteSpace(natsUrl))
    {
        builder.Services.AddSingleton<INatsConnection>(_ => new NatsConnection(new NatsOpts { Url = natsUrl }));
        builder.Services.AddSingleton<IOutboxPublisher, NatsOutboxPublisher>();
    }
    else
    {
        builder.Services.AddSingleton<IOutboxPublisher, RecordingOutboxPublisher>();
    }

    builder.Services.AddSingleton<OutboxRelay>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxRelay>());
    builder.Services.AddSingleton<OutboxReplay>();
    builder.Services.AddSingleton<OutboxRetentionJob>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxRetentionJob>());
    builder.Services.AddHostedService<JetStreamBootstrap>();
}

static void ConfigureApp(WebApplication app)
{
    app.UseMiddleware<SchemaVersionMiddleware>();
    app.MapDefaultEndpoints("wms-core");
    app.MapInternalApi();
    app.MapWarehouseApi();
    app.MapArticleApi();
}

public partial class Program;
