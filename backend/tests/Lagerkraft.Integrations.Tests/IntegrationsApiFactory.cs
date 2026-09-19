using System.Security.Cryptography;
using Lagerkraft.Integrations.Events;
using Lagerkraft.Integrations.Webhooks;
using Lagerkraft.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Lagerkraft.Integrations.Tests;

public sealed class IntegrationsApiFactory : WebApplicationFactory<Program>
{
    public FakeClock Clock { get; } = new();
    public required string PlatformConnectionString { get; init; }
    public required string NatsUrl { get; init; }

    private readonly string _privateKeyPem = RSA.Create(2048).ExportRSAPrivateKeyPem();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:platform", PlatformConnectionString);
        builder.UseSetting("ConnectionStrings:nats", NatsUrl);
        builder.UseSetting("Jwt:PrivateKeyPem", _privateKeyPem);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
            services.RemoveAll<IHostedService>();
        });
    }

    public EventConsumerJob Consumer => Services.GetRequiredService<EventConsumerJob>();
    public WebhookDeliveryJob Delivery => Services.GetRequiredService<WebhookDeliveryJob>();
}
