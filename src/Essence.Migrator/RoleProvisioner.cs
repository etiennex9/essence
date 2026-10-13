using Microsoft.Extensions.Logging;
using Npgsql;

namespace Essence.Migrator;

/// <summary>
/// Creates (or updates the password of) the two application roles and grants them their privileges.
/// Runs after migrations so that "ALL TABLES" includes every table. Idempotent.
/// </summary>
internal static class RoleProvisioner
{
    public const string WriterRole = "essence_writer";
    public const string ReaderRole = "essence_reader";

    public static async Task ProvisionAsync(
        string adminConnectionString,
        string writerPassword,
        string readerPassword,
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

            GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO {WriterRole};
            GRANT DELETE ON current_prices TO {WriterRole};
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {WriterRole};
            REVOKE INSERT, UPDATE ON "__EFMigrationsHistory" FROM {WriterRole};
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Roles {WriterRole} and {ReaderRole} are provisioned.", WriterRole, ReaderRole);
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
