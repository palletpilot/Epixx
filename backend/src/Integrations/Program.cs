using System.Text.Json.Serialization;
using Lagerkraft.Integrations.Auth;
using Lagerkraft.Integrations.Data;
using Lagerkraft.Integrations.Events;
using Lagerkraft.Integrations.Webhooks;
using Lagerkraft.Shared;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddDbContext<IntegrationsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("platform"))
        .UseSnakeCaseNamingConvention());
builder.Services.AddSingleton<WebhookPoster>();
builder.Services.AddSingleton<WebhookDeliveryJob>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookDeliveryJob>());
builder.Services.AddOpenApi("integrations");

if (!IsOpenApiDocumentGeneration())
{
    builder.Services.AddIntegrationsAuth(builder.Configuration);
}
else
{
    builder.Services.AddAuthorization();
}

var natsUrl = builder.Configuration.GetConnectionString("nats")
    ?? builder.Configuration["NATS_URL"];
if (!string.IsNullOrWhiteSpace(natsUrl))
{
    builder.Services.AddSingleton<INatsConnection>(_ => new NatsConnection(new NatsOpts { Url = natsUrl }));
    builder.Services.AddSingleton<EventConsumerJob>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<EventConsumerJob>());
}

var app = builder.Build();
app.MapDefaultEndpoints("integrations");
if (!IsOpenApiDocumentGeneration())
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapWebhooksApi();
app.Run();

static bool IsOpenApiDocumentGeneration()
{
    var entry = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "";
    return entry.Contains("GetDocument", StringComparison.OrdinalIgnoreCase);
}

public partial class Program;
