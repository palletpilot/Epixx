using Lagerkraft.Platform.Email;
using Lagerkraft.Platform.Provisioning;
using Lagerkraft.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Lagerkraft.Platform.Tests;

public sealed class PlatformApiFactory(PostgresFixture postgres) : WebApplicationFactory<Program>
{
    public const string InternalToken = "test-internal-token";
    public const string Kek = "lagerkraft-dev-kek-not-a-secret!";

    public FakeClock Clock { get; } = new();
    public CapturingEmailSender Email { get; } = new();
    public RecordingDatabaseCreator DatabaseCreator { get; } = new();
    public RecordingMigrateClient MigrateClient { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:platform", postgres.ConnectionString);
        builder.UseSetting("Crypto:Kek", Kek);
        builder.UseSetting("Internal:Token", InternalToken);
        builder.UseSetting("Email:Provider", "mailpit");
        builder.UseSetting("PublicBaseUrl", "http://localhost");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);

            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Email);

            services.RemoveAll<ITenantDatabaseCreator>();
            services.AddSingleton<ITenantDatabaseCreator>(DatabaseCreator);

            services.RemoveAll<IWmsCoreMigrateClient>();
            services.AddSingleton<IWmsCoreMigrateClient>(MigrateClient);

            // Jobs run via TickForTests; disable background loops in tests.
            services.RemoveAll<IHostedService>();
        });
    }
}

public sealed class RecordingDatabaseCreator : ITenantDatabaseCreator
{
    public int Calls { get; private set; }
    public bool Fail { get; set; }

    public Task CreateAsync(Guid tenantId, string databaseName, CancellationToken ct)
    {
        Calls++;
        if (Fail)
        {
            throw new InvalidOperationException("CREATE DATABASE failed (test)");
        }

        return Task.CompletedTask;
    }
}

public sealed class RecordingMigrateClient : IWmsCoreMigrateClient
{
    public int Calls { get; private set; }
    public bool FailWithServerError { get; set; }

    public Task MigrateAsync(Guid tenantId, CancellationToken ct)
    {
        Calls++;
        if (FailWithServerError)
        {
            throw new HttpRequestException(
                "wms-core migrate returned 500",
                null,
                System.Net.HttpStatusCode.InternalServerError);
        }

        return Task.CompletedTask;
    }
}