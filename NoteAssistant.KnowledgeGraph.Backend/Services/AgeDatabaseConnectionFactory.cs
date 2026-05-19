using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Npgsql;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public sealed class DatabaseOptions
{
    public string? Host { get; init; }
    public int Port { get; init; } = 5432;
    public string? Database { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string SchemaName { get; init; } = "kg_data";
    public string GraphName { get; init; } = "knowledge_graph";
    public string[] Extensions { get; init; } = ["age", "vector", "pg_diskann"];
    public DatabaseAuthOptions Auth { get; init; } = new();
}

public sealed class DatabaseAuthOptions
{
    public string Mode { get; init; } = "Password";
    public string EntraScope { get; init; } = "https://ossrdbms-aad.database.windows.net/.default";
    public string? TenantId { get; init; }
}

public interface IAgeDatabaseConnectionFactory
{
    bool IsConfigured { get; }
    Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken);
}

public sealed class AgeDatabaseConnectionFactory : IAgeDatabaseConnectionFactory
{
    private readonly string? _connectionString;
    private readonly DatabaseOptions _databaseOptions;
    private readonly TokenCredential _credential;

    public AgeDatabaseConnectionFactory(IConfiguration configuration, IOptions<DatabaseOptions> databaseOptions)
    {
        _connectionString = configuration.GetConnectionString("AgeDatabase");
        _databaseOptions = databaseOptions.Value;
        _credential = CreateCredential(_databaseOptions.Auth);
    }

    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(_connectionString)
           || (!string.IsNullOrWhiteSpace(_databaseOptions.Host)
               && !string.IsNullOrWhiteSpace(_databaseOptions.Database)
               && !string.IsNullOrWhiteSpace(_databaseOptions.Username));

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Database settings are not configured.");
        }

        var connectionString = string.IsNullOrWhiteSpace(_connectionString)
            ? BuildConnectionString(_databaseOptions)
            : _connectionString;

        if (UseEntraId())
        {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext([_databaseOptions.Auth.EntraScope]),
                cancellationToken);

            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Password = token.Token,
                SslMode = SslMode.Require
            };
            connectionString = builder.ConnectionString;
        }

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var schema = NormalizeSchemaName(_databaseOptions.SchemaName);
        await EnsureAgeSearchPathAsync(connection, schema, cancellationToken);
        return connection;
    }

    private bool UseEntraId()
        => string.Equals(_databaseOptions.Auth.Mode, "EntraId", StringComparison.OrdinalIgnoreCase);

    private static TokenCredential CreateCredential(DatabaseAuthOptions auth)
    {
        if (string.IsNullOrWhiteSpace(auth.TenantId))
        {
            return new DefaultAzureCredential();
        }

        var options = new DefaultAzureCredentialOptions
        {
            TenantId = auth.TenantId
        };

        return new DefaultAzureCredential(options);
    }

    private static string BuildConnectionString(DatabaseOptions options)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host,
            Port = options.Port,
            Database = options.Database,
            Username = options.Username,
            Password = options.Password,
            SslMode = SslMode.Prefer
        };

        return builder.ConnectionString;
    }

    private static async Task EnsureAgeSearchPathAsync(NpgsqlConnection connection, string schemaName, CancellationToken cancellationToken)
    {
        try
        {
            await using var create = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS age;", connection);
            await create.ExecuteNonQueryAsync(cancellationToken);

            await using var createSchema = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS {schemaName};", connection);
            await createSchema.ExecuteNonQueryAsync(cancellationToken);

            await using var command = new NpgsqlCommand($"SET search_path = {schemaName}, public, ag_catalog;", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException)
        {
            // AGE extension not installed on this server; keep the default search_path.
        }
    }

    private static string NormalizeSchemaName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "kg_data";
        }

        var trimmed = value.Trim();
        foreach (var ch in trimmed)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_')
            {
                return "kg_data";
            }
        }

        return trimmed;
    }
}
