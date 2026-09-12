using Npgsql;

namespace Lagerkraft.Platform.Provisioning;

public sealed class PostgresCreateDatabase(IConfiguration configuration) : ITenantDatabaseCreator
{
    public async Task CreateAsync(Guid tenantId, string databaseName, CancellationToken ct)
    {
        var platform = configuration.GetConnectionString("platform")
            ?? throw new InvalidOperationException("ConnectionStrings:platform is missing.");
        var builder = new NpgsqlConnectionStringBuilder(platform) { Database = "postgres" };
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        await using (var exists = new NpgsqlCommand(
            "SELECT 1 FROM pg_database WHERE datname = @name", conn))
        {
            exists.Parameters.AddWithValue("name", databaseName);
            var found = await exists.ExecuteScalarAsync(ct);
            if (found is not null)
            {
                return;
            }
        }

        // Database names cannot be parameterized; tenant id is a GUID so this is safe.
        await using var create = new NpgsqlCommand($"""CREATE DATABASE "{databaseName}" """, conn);
        await create.ExecuteNonQueryAsync(ct);
    }
}
