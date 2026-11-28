using Microsoft.Extensions.Logging;
using Npgsql;

namespace Essence.Migrator;

/// <summary>
/// Creates (or updates the password of) the application roles and grants them their privileges: the ingest's writer, the
/// API's reader, and with a password for it, the API's usage role, which writes the usage counts and nothing else (README →
/// Usage). Runs after migrations so that "ALL TABLES" includes every table. Idempotent.
/// </summary>
internal static class RoleProvisioner
{
    public const string WriterRole = "essence_writer";
    public const string ReaderRole = "essence_reader";
    public const string UsageRole = "essence_usage";

    /// <param name="usagePassword">Null or empty: no usage role is created (an existing one is left as it is).</param>
    public static async Task ProvisionAsync(
        string adminConnectionString,
        string writerPassword,
        string readerPassword,
        string? usagePassword,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // A data source without a logger factory: these statements contain passwords and must not be logged.
        await using var dataSource = NpgsqlDataSource.Create(adminConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureLoginRoleAsync(connection, WriterRole, writerPassword, cancellationToken);
        await EnsureLoginRoleAsync(connection, ReaderRole, readerPassword, cancellationToken);

        var database = QuoteIdentifier(connection.Database);
        await ExecuteAsync(connection, $"""
            GRANT CONNECT ON DATABASE {database} TO {WriterRole}, {ReaderRole};
            GRANT USAGE ON SCHEMA public TO {WriterRole}, {ReaderRole};

            GRANT SELECT ON ALL TABLES IN SCHEMA public TO {ReaderRole};
            -- The day's salt would let whoever reads usage_web find a visitor's address by trying addresses.
            REVOKE SELECT ON usage_salts FROM {ReaderRole};

            GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO {WriterRole};
            GRANT DELETE ON current_prices, station_hours, position_candidates TO {WriterRole};
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {WriterRole};
            REVOKE INSERT, UPDATE ON "__EFMigrationsHistory" FROM {WriterRole};
            -- The usage counts are the API's, through its usage role.
            REVOKE ALL ON usage_android, usage_web, usage_salts FROM {WriterRole};
            """, cancellationToken);

        if (!string.IsNullOrEmpty(usagePassword))
        {
            await EnsureLoginRoleAsync(connection, UsageRole, usagePassword, cancellationToken);
            await ExecuteAsync(connection, $"""
                GRANT CONNECT ON DATABASE {database} TO {UsageRole};
                GRANT USAGE ON SCHEMA public TO {UsageRole};
                GRANT SELECT, INSERT ON usage_android, usage_web TO {UsageRole};
                GRANT SELECT, INSERT, DELETE ON usage_salts TO {UsageRole};
                """, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        if (string.IsNullOrEmpty(usagePassword))
        {
            logger.LogInformation("Roles {WriterRole} and {ReaderRole} are provisioned; without Roles:UsagePassword, no {UsageRole}.", WriterRole, ReaderRole, UsageRole);
        }
        else
        {
            logger.LogInformation("Roles {WriterRole}, {ReaderRole} and {UsageRole} are provisioned.", WriterRole, ReaderRole, UsageRole);
        }
    }

    private static async Task EnsureLoginRoleAsync(NpgsqlConnection connection, string role, string password, CancellationToken cancellationToken)
    {
        // CREATE/ALTER ROLE can't take parameters: format() builds the statement server-side, quoting with %I and %L.
        await using var build = new NpgsqlCommand("""
            SELECT format(
                CASE WHEN EXISTS (SELECT FROM pg_roles WHERE rolname = $1)
                     THEN 'ALTER ROLE %I WITH LOGIN PASSWORD %L'
                     ELSE 'CREATE ROLE %I WITH LOGIN PASSWORD %L' END,
                $1, $2)
            """, connection)
        {
            Parameters = { new() { Value = role }, new() { Value = password } },
        };
        var statement = (string)(await build.ExecuteScalarAsync(cancellationToken))!;
        await ExecuteAsync(connection, statement, cancellationToken);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
