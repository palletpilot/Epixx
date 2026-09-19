var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("pg")
    .WithImageTag("17")
    .WithPgAdmin();

// Resource names cannot contain underscores and must not collide with service names.
var platformDb = postgres.AddDatabase("platform-db", "platform");
var tenantMigrateDb = postgres.AddDatabase("tenant-migrate", "tenant_migrate");

var nats = builder.AddNats("nats")
    .WithImageTag("2.11")
    .WithJetStream();

builder.AddContainer("mailpit", "axllent/mailpit")
    .WithHttpEndpoint(targetPort: 8025, name: "http")
    .WithEndpoint(port: 1025, targetPort: 1025, name: "smtp");

// Shared cluster-internal token for /internal/* (dev). Prod uses a K8s secret.
const string internalToken = "lagerkraft-internal-dev";

var platform = builder.AddProject<Projects.Lagerkraft_Platform>("platform")
    .WithReference(platformDb, connectionName: "platform")
    .WithReference(nats)
    .WithEnvironment("Internal__Token", internalToken)
    .WaitFor(postgres)
    .WaitFor(nats);

var wmsCore = builder.AddProject<Projects.Lagerkraft_WmsCore_Api>("wms-core")
    .WithReference(tenantMigrateDb, connectionName: "tenant_migrate")
    .WithReference(nats)
    .WithReference(platform)
    .WithEnvironment("Internal__Token", internalToken)
    .WithEnvironment("Platform__BaseUrl", "https+http://platform")
    .WaitFor(postgres)
    .WaitFor(nats)
    .WaitFor(platform);

// Platform B3 migrate client (Program reads Services:WmsCore; empty => Noop).
platform
    .WithReference(wmsCore)
    .WithEnvironment("Services__WmsCore", "https+http://wms-core");

// Sync D1: Platform JWKS + internal HTTP to Platform/WmsCore (same token as migrate).
builder.AddProject<Projects.Lagerkraft_SyncGateway>("sync-gateway")
    .WithReference(nats)
    .WithReference(platform)
    .WithReference(wmsCore)
    .WithEnvironment("Internal__Token", internalToken)
    .WithEnvironment("Services__Platform", "https+http://platform")
    .WithEnvironment("Services__WmsCore", "https+http://wms-core")
    .WithEnvironment("Jwt__JwksUrl", "https+http://platform/.well-known/jwks.json")
    .WaitFor(nats)
    .WaitFor(platform)
    .WaitFor(wmsCore);

builder.AddProject<Projects.Lagerkraft_Integrations>("integrations")
    .WithReference(platformDb, connectionName: "platform")
    .WithReference(nats)
    .WithReference(platform)
    .WithEnvironment("Jwt__JwksUrl", "https+http://platform/.well-known/jwks.json")
    .WaitFor(postgres)
    .WaitFor(nats)
    .WaitFor(platform);

builder.Build().Run();
