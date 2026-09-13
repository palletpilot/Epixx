using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Relay;
using Lagerkraft.WmsCore.Api.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Lagerkraft.WmsCore.Tests;

public sealed class WmsCoreApiFactory : WebApplicationFactory<Program>
{
    public const string InternalToken = "test-internal-token";

    public FakePlatformTenantClient Platform { get; } = new();
    public FakeClock? Clock { get; set; }
    public RecordingOutboxPublisher Publisher { get; } = new();

    /// <summary>Matches Aspire Development: scoped services cannot be pulled from the root provider.</summary>
    public bool ValidateScopes { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Internal:Token", InternalToken);
        builder.UseSetting("Platform:BaseUrl", "http://platform.test");
        if (ValidateScopes)
        {
            builder.UseDefaultServiceProvider((_, o) =>
            {
                o.ValidateScopes = true;
                o.ValidateOnBuild = true;
            });
        }
        builder.ConfigureTestServices(services =>
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IPlatformTenantClient)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<IPlatformTenantClient>(Platform);

            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IOutboxPublisher)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<IOutboxPublisher>(Publisher);
            services.AddSingleton(Publisher);

            if (Clock is not null)
            {
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IClock)).ToList())
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton<IClock>(Clock);
            }
        });
    }
}
