using System.CommandLine;
using FluentValidation;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Commands;
using Lagerkraft.WmsCore.Api.Commands.Probe;
using Lagerkraft.WmsCore.Api.Commands.Tasks;
using Lagerkraft.WmsCore.Api.Jobs;
using Lagerkraft.WmsCore.Api.Warehouses;
using Lagerkraft.WmsCore.Api.Internal;
using Lagerkraft.WmsCore.Api.Migrations;
using System.Text.Json.Serialization;
using Lagerkraft.WmsCore.Api.Tenancy;

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
    var tenantOption = new Option<Guid?>("--tenant") { Description = "Tenant id to migrate" };
    var allOption = new Option<bool>("--all") { Description = "Migrate all tenants (not yet wired)" };
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
            throw new ArgumentException("--tenant <guid> is required until --all is wired");
        }

        await migrator.MigrateTenantAsync(tenant.Value, ct);
    });
    root.Subcommands.Add(migrateCommand);
    root.Subcommands.Add(new Command("relay", "Outbox relay (C4)"));
    root.Subcommands.Add(new Command("replay", "Outbox replay (C4)"));
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
    builder.Services.AddValidatorsFromAssemblyContaining<ProbeValidator>();
    builder.Services.AddSingleton<CommandRegistry>(sp =>
    {
        var registry = new CommandRegistry();
        registry.Register(ActivatorUtilities.CreateInstance<ProbeHandler>(sp));
        registry.Register(new ProbeV0ToV1Upcaster());
        registry.Register(ActivatorUtilities.CreateInstance<CreateTaskHandler>(sp));
        registry.Register(ActivatorUtilities.CreateInstance<ClaimTaskHandler>(sp));
        registry.Register(ActivatorUtilities.CreateInstance<ReleaseTaskHandler>(sp));
        registry.Register(ActivatorUtilities.CreateInstance<CompleteTaskHandler>(sp));
        return registry;
    });
    builder.Services.AddSingleton<CommandDispatcher>();
    builder.Services.AddSingleton<AssignmentSweepJob>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AssignmentSweepJob>());
}

static void ConfigureApp(WebApplication app)
{
    app.UseMiddleware<SchemaVersionMiddleware>();
    app.MapDefaultEndpoints("wms-core");
    app.MapInternalApi();
    app.MapWarehouseApi();
}

public partial class Program;


