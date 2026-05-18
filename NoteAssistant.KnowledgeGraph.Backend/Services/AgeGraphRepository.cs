using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public sealed class AgeGraphRepository(IConfiguration configuration, ILogger<AgeGraphRepository> logger)
{
    private const int MaxTraversalHops = 3;
    private const int MaxVectorResultLimit = 50;
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
        if (!IsReadOnlyCypher(normalized))
        {
            return new GraphQueryResponse(false, "Only read-only Cypher is allowed (MATCH/OPTIONAL MATCH/WITH/UNWIND/RETURN/ORDER BY/LIMIT/SKIP/WHERE).", [], [], []);
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

    public async Task<HybridRetrievalResponse> ExecuteHybridRetrievalAsync(HybridRetrievalRequest request, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return new HybridRetrievalResponse(false, "ConnectionStrings:AgeDatabase is not configured in backend settings.", [], [], [], string.Empty, "Graph -> Vector -> LLM");
        }

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return new HybridRetrievalResponse(false, "Query is required.", [], [], [], string.Empty, "Graph -> Vector -> LLM");
        }

        var detectedEntities = DetectEntities(request.Query);

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var graphEntities = await ExpandEntitiesByGraphAsync(connection, request.GraphName, detectedEntities, request.MaxHops, cancellationToken);
            var vector = request.QueryEmbedding is { Length: > 0 } ? request.QueryEmbedding : CreatePseudoEmbedding(request.Query, 1536);
            var chunks = await QueryChunksByEntitiesAndVectorAsync(connection, graphEntities, vector, request.Limit, cancellationToken);
            var prompt = BuildPromptContext(graphEntities, chunks);

            return new HybridRetrievalResponse(true, null, detectedEntities, graphEntities, chunks, prompt, "Graph -> Vector -> LLM");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hybrid retrieval pipeline failed.");
            return new HybridRetrievalResponse(false, ex.Message, detectedEntities, [], [], string.Empty, "Graph -> Vector -> LLM");
        }
    }

    private static bool IsReadOnlyCypher(string query)
    {
        if (query.Contains(';', StringComparison.Ordinal) || query.Contains("--", StringComparison.Ordinal) || query.Contains("/*", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Regex.IsMatch(query, @"^(MATCH|OPTIONAL MATCH|WITH|UNWIND)\b", RegexOptions.IgnoreCase))
        {
            return false;
        }

        return !Regex.IsMatch(query, @"\b(create|merge|delete|set|drop|remove|call|load)\b", RegexOptions.IgnoreCase);
    }

    private static List<string> DetectEntities(string query)
    {
        var known = new[] { "Microsoft", "OpenAI", "Azure", "PostgreSQL", "Apache AGE", "DiskANN", "pgvector" };
        var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in known)
        {
            if (query.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                entities.Add(candidate);
            }
        }

        foreach (Match match in Regex.Matches(query, @"\b[A-Z][A-Za-z0-9]+(?:\s+[A-Z][A-Za-z0-9]+)?\b"))
        {
            var value = match.Value.Trim();
            if (value.Length is >= 3 and <= 80)
            {
                entities.Add(value);
            }
        }

        return entities.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    private static async Task<List<string>> ExpandEntitiesByGraphAsync(
        NpgsqlConnection connection,
        string graphName,
        IReadOnlyCollection<string> seedEntities,
        int hops,
        CancellationToken cancellationToken)
    {
        if (seedEntities.Count == 0)
        {
            return [];
        }

        var maxHops = Math.Clamp(hops, 1, MaxTraversalHops);
        var expanded = new HashSet<string>(seedEntities, StringComparer.OrdinalIgnoreCase);

        foreach (var seed in seedEntities)
        {
            if (!Regex.IsMatch(seed, @"^[a-zA-Z0-9\s._-]{1,80}$"))
            {
                continue;
            }

            var cypher = $"MATCH (a {{name:\"{EscapeCypherLiteral(seed)}\"}})-[*1..{maxHops}]-(b) RETURN DISTINCT b LIMIT 50";
            const string sql = "SELECT * FROM cypher(@graph_name, @cypher_query) AS (node agtype);";

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("graph_name", graphName);
            command.Parameters.AddWithValue("cypher_query", cypher);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                var value = reader.GetString(0);
                if (TryExtractVertexName(value, out var name) && !string.IsNullOrWhiteSpace(name))
                {
                    expanded.Add(name);
                }
            }

            await reader.CloseAsync();
        }

        return expanded.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<List<HybridChunkResultDto>> QueryChunksByEntitiesAndVectorAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<string> entities,
        float[] queryEmbedding,
        int limit,
        CancellationToken cancellationToken)
    {
        if (entities.Count == 0)
        {
            return [];
        }

        var constrainedLimit = Math.Clamp(limit, 1, MaxVectorResultLimit);
        var vectorLiteral = ToVectorLiteral(queryEmbedding, 1536);
        const string sql = """
                           SELECT c.id,
                                  c.document_id,
                                  c.chunk_index,
                                  c.content,
                                  c.embedding <=> CAST(@query_vector AS vector) AS distance
                           FROM chunks c
                           JOIN chunk_entities ce ON ce.chunk_id = c.id
                           JOIN entities e ON e.id = ce.entity_id
                           WHERE e.name = ANY(@entity_names)
                             AND c.embedding IS NOT NULL
                           ORDER BY c.embedding <=> CAST(@query_vector AS vector)
                           LIMIT @limit;
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("entity_names", entities.ToArray());
        command.Parameters.AddWithValue("query_vector", vectorLiteral);
        command.Parameters.AddWithValue("limit", constrainedLimit);

        var chunks = new List<HybridChunkResultDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var documentId = checked((int)reader.GetInt64(1));
            var chunkIndex = reader.GetInt32(2);
            var content = reader.GetString(3);
            double? distance = reader.IsDBNull(4) ? null : reader.GetDouble(4);
            chunks.Add(new HybridChunkResultDto(id, documentId, chunkIndex, content, distance));
        }

        await reader.CloseAsync();
        return chunks;
    }

    private static string BuildPromptContext(IReadOnlyCollection<string> graphEntities, IReadOnlyCollection<HybridChunkResultDto> chunks)
    {
        var graphFactSection = graphEntities.Count == 0
            ? "No graph entities found."
            : $"Graph entities ({graphEntities.Count}): {string.Join(", ", graphEntities)}";

        var chunkSection = chunks.Count == 0
            ? "No vector-ranked chunks found (ensure embeddings are populated in chunks.embedding)."
            : string.Join(Environment.NewLine, chunks.Select(c => $"- [doc:{c.DocumentId} chunk:{c.ChunkIndex}] {c.Content}"));

        return $"{graphFactSection}{Environment.NewLine}Relevant chunks:{Environment.NewLine}{chunkSection}";
    }

    private static float[] CreatePseudoEmbedding(string text, int dimension)
    {
        var hash = text.Aggregate(17, (current, c) => current * 31 + c);
        var random = new Random(hash);
        var vector = new float[dimension];
        for (var i = 0; i < dimension; i++)
        {
            vector[i] = (float)(random.NextDouble() * 2d - 1d);
        }

        return vector;
    }

    private static string ToVectorLiteral(float[] input, int dimension)
    {
        var vector = new float[dimension];
        var length = Math.Min(input.Length, dimension);
        Array.Copy(input, vector, length);
        return $"[{string.Join(",", vector.Select(v => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)))}]";
    }

    private static bool TryExtractVertexName(string value, out string name)
    {
        name = string.Empty;

        if (!value.Contains("::vertex", StringComparison.Ordinal))
        {
            return false;
        }

        var json = value.Replace("::vertex", string.Empty, StringComparison.Ordinal).Trim();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty("name", out var nameValue))
        {
            name = nameValue.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(name);
        }

        return false;
    }

    private static string EscapeCypherLiteral(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

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
