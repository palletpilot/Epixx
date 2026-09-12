using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Lagerkraft.WmsCore.Tests;

public sealed class WmsCoreApiFactory : WebApplicationFactory<Program>
{
    public const string InternalToken = "test-internal-token";

    public FakePlatformTenantClient Platform { get; } = new();
    public FakeClock? Clock { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Internal:Token", InternalToken);
        builder.UseSetting("Platform:BaseUrl", "http://platform.test");
        builder.ConfigureTestServices(services =>
        {
            var existingPlatform = services.Where(d => d.ServiceType == typeof(IPlatformTenantClient)).ToList();
            foreach (var descriptor in existingPlatform)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<IPlatformTenantClient>(Platform);

            if (Clock is not null)
            {
                var clocks = services.Where(d => d.ServiceType == typeof(IClock)).ToList();
                foreach (var descriptor in clocks)
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton<IClock>(Clock);
            }
        });
    }
}
