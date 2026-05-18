using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public sealed class AgeGraphRepository(IConfiguration configuration, ILogger<AgeGraphRepository> logger)
{
    private readonly string? _connectionString = configuration.GetConnectionString("AgeDatabase");

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

    public async Task<(bool Success, string? ErrorMessage)> TryExecuteIngestionPlanAsync(GraphIngestionPlan plan, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return (false, "Connection string is not configured.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            foreach (var statement in plan.SqlStatements)
            {
                await using var command = new NpgsqlCommand(statement, connection)
                {
                    CommandType = CommandType.Text
                };
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute ingestion SQL plan.");
            return (false, ex.Message);
        }
    }

    public async Task<GraphQueryResponse> ExecuteSelectQueryAsync(GraphQueryRequest request, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return new GraphQueryResponse(false, "ConnectionStrings:AgeDatabase is not configured in backend settings.", [], [], []);
        }

        if (string.IsNullOrWhiteSpace(request.Cypher))
        {
            return new GraphQueryResponse(false, "Cypher query text is required.", [], [], []);
        }

        var normalized = request.Cypher.Trim();
        if (Regex.IsMatch(normalized, @"\b(create|merge|delete|set|drop|remove|call)\b", RegexOptions.IgnoreCase))
        {
            return new GraphQueryResponse(false, "Query editor is read-only. Use MATCH/RETURN style Cypher.", [], [], []);
        }

        try
        {
            var rows = new List<Dictionary<string, string?>>();
            var nodes = new Dictionary<string, GraphNodeDto>(StringComparer.Ordinal);
            var edges = new List<GraphEdgeDto>();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            const string sql = "SELECT * FROM cypher(@graph_name, @cypher_query) AS (result agtype);";
            await using var command = new NpgsqlCommand(sql, connection)
            {
                CommandType = CommandType.Text
            };
            command.Parameters.AddWithValue("graph_name", request.GraphName);
            command.Parameters.AddWithValue("cypher_query", normalized);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, string?>(StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var key = reader.GetName(i);
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
                    row[key] = value;
                    AddGraphPrimitives(value, nodes, edges);
                }
                rows.Add(row);
            }

            return new GraphQueryResponse(true, null, rows, nodes.Values.ToList(), edges);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query execution failed.");
            return new GraphQueryResponse(false, ex.Message, [], [], []);
        }
    }

    private static void AddGraphPrimitives(string? agTypeValue, IDictionary<string, GraphNodeDto> nodes, ICollection<GraphEdgeDto> edges)
    {
        if (string.IsNullOrWhiteSpace(agTypeValue))
        {
            return;
        }

        if (agTypeValue.Contains("::vertex", StringComparison.Ordinal))
        {
            ParseVertex(agTypeValue, nodes);
        }
        else if (agTypeValue.Contains("::edge", StringComparison.Ordinal))
        {
            ParseEdge(agTypeValue, edges);
        }
    }

    private static void ParseVertex(string value, IDictionary<string, GraphNodeDto> nodes)
    {
        var json = value.Replace("::vertex", string.Empty, StringComparison.Ordinal).Trim();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var id = root.TryGetProperty("id", out var idValue) ? idValue.ToString() : Guid.NewGuid().ToString("N");
        var label = root.TryGetProperty("label", out var labelValue) ? labelValue.GetString() ?? "Node" : "Node";

        var title = label;
        if (root.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty("name", out var nameValue))
        {
            title = $"{label}: {nameValue.GetString()}";
        }

        nodes[id] = new GraphNodeDto(id, label, title);
    }

    private static void ParseEdge(string value, ICollection<GraphEdgeDto> edges)
    {
        var json = value.Replace("::edge", string.Empty, StringComparison.Ordinal).Trim();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var source = root.TryGetProperty("start_id", out var start) ? start.ToString() : string.Empty;
        var target = root.TryGetProperty("end_id", out var end) ? end.ToString() : string.Empty;
        var label = root.TryGetProperty("label", out var labelValue) ? labelValue.GetString() ?? string.Empty : string.Empty;

        if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(target))
        {
            edges.Add(new GraphEdgeDto(source, target, label));
        }
    }
}
