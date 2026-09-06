using Lagerkraft.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Lagerkraft.Platform.Tests;

public sealed class PlatformApiFactory(PostgresFixture postgres) : WebApplicationFactory<Program>
{
    public const string InternalToken = "test-internal-token";
    public const string Kek = "lagerkraft-dev-kek-not-a-secret!";

    public FakeClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:platform", postgres.ConnectionString);
        builder.UseSetting("Crypto:Kek", Kek);
        builder.UseSetting("Internal:Token", InternalToken);
        builder.ConfigureTestServices(services =>
        {
            var existing = services.Where(d => d.ServiceType == typeof(IClock)).ToList();
            foreach (var descriptor in existing)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<IClock>(Clock);
        });
    }
}
