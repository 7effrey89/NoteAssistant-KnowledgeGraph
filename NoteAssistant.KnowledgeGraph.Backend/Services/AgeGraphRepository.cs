using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public sealed class AgeGraphRepository(ILogger<AgeGraphRepository> logger, IFoundryInferenceClient foundry, IAgeDatabaseConnectionFactory connectionFactory)
{
    private const int MaxTraversalHops = 3;
    private const int MaxVectorResultLimit = 50;
    private readonly IFoundryInferenceClient _foundry = foundry;
    private readonly IAgeDatabaseConnectionFactory _connectionFactory = connectionFactory;

    public bool IsConfigured => _connectionFactory.IsConfigured;

    public async Task<(bool Success, string? ErrorMessage)> TryExecuteIngestionPlanAsync(GraphIngestionPlan plan, CancellationToken cancellationToken)
    {
        return await TryExecuteStatementsAsync(plan.SqlStatements, cancellationToken);
    }

    public async Task<(bool Success, string? ErrorMessage)> TryExecuteStatementsAsync(IEnumerable<string> statements, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return (false, "Connection string is not configured.");
        }

        try
        {
            await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

            foreach (var statement in statements)
            {
                if (string.IsNullOrWhiteSpace(statement))
                {
                    continue;
                }

                await using var command = new NpgsqlCommand(statement, connection)
                {
                    CommandType = CommandType.Text
                };

                if (statement.Contains("cypher(", StringComparison.OrdinalIgnoreCase))
                {
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        // Drain cypher result set to ensure execution completes.
                    }
                }
                else
                {
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute SQL statements.");
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? ErrorMessage, IngestionExecutionLogDto Log)> TryExecuteStatementsWithLogAsync(
        IEnumerable<string> statements,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var steps = new List<StatementExecutionDto>();
        var index = 0;

        if (!IsConfigured)
        {
            var log = new IngestionExecutionLogDto(startedAt, DateTimeOffset.UtcNow, 0, 0, 0, steps);
            return (false, "Connection string is not configured.", log);
        }

        try
        {
            await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

            foreach (var statement in statements)
            {
                if (string.IsNullOrWhiteSpace(statement))
                {
                    continue;
                }

                index++;
                var stepStart = Stopwatch.StartNew();
                var statementType = statement.Contains("cypher(", StringComparison.OrdinalIgnoreCase) ? "cypher" : "sql";
                try
                {
                    await using var command = new NpgsqlCommand(statement, connection)
                    {
                        CommandType = CommandType.Text
                    };

                    if (statementType == "cypher")
                    {
                        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            // Drain result set.
                        }
                    }
                    else
                    {
                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }

                    stepStart.Stop();
                    steps.Add(new StatementExecutionDto(index, statementType, true, (int)stepStart.ElapsedMilliseconds, null, Truncate(statement, 300)));
                }
                catch (Exception ex)
                {
                    stepStart.Stop();
                    steps.Add(new StatementExecutionDto(index, statementType, false, (int)stepStart.ElapsedMilliseconds, ex.Message, Truncate(statement, 300)));
                    var failedLog = new IngestionExecutionLogDto(startedAt, DateTimeOffset.UtcNow, steps.Count, steps.Count - 1, 1, steps);
                    return (false, ex.Message, failedLog);
                }
            }

            var succeeded = steps.Count(s => s.Success);
            var failed = steps.Count - succeeded;
            var completedLog = new IngestionExecutionLogDto(startedAt, DateTimeOffset.UtcNow, steps.Count, succeeded, failed, steps);
            return (true, null, completedLog);
        }
        catch (Exception ex)
        {
            var log = new IngestionExecutionLogDto(startedAt, DateTimeOffset.UtcNow, steps.Count, steps.Count, 0, steps);
            logger.LogError(ex, "Failed to execute SQL statements with log.");
            return (false, ex.Message, log);
        }
    }

    public async Task<GraphQueryResponse> ExecuteSelectQueryAsync(GraphQueryRequest request, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return new GraphQueryResponse(false, "Database settings are not configured (ConnectionStrings:AgeDatabase or Database section).", [], [], []);
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

            await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

            var (cypherQuery, columnDefinition) = NormalizeCypherQuery(normalized);
            var sql = BuildCypherSql(request.GraphName, cypherQuery, columnDefinition);
            await using var command = new NpgsqlCommand(sql, connection)
            {
                CommandType = CommandType.Text
            };

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, string?>(StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var key = reader.GetName(i);
                    var value = GetFieldValueAsString(reader, i);
                    row[key] = value;
                    AddGraphPrimitives(value, nodes, edges);
                }
                rows.Add(row);
            }

            if (nodes.Count == 0)
            {
                var fallback = await TryBuildFallbackGraphAsync(connection, cancellationToken);
                if (fallback.Nodes.Count > 0)
                {
                    return new GraphQueryResponse(true, null, rows, fallback.Nodes, fallback.Edges);
                }
            }

            if (nodes.Count == 0 && rows.Count > 0)
            {
                var sample = string.Join(" | ", rows[0].Select(kv => $"{kv.Key}={Truncate(kv.Value, 200)}"));
                logger.LogWarning("No graph primitives parsed from cypher result. Sample row: {Sample}", sample);
            }

            return new GraphQueryResponse(true, null, rows, nodes.Values.ToList(), edges);
        }
        catch (PostgresException pgEx) when (pgEx.SqlState == "42804")
        {
            logger.LogError(pgEx, "Query execution failed.");
            if (!TryRewriteReturnAsMap(normalized, out var rewritten))
            {
                return new GraphQueryResponse(false, "Cypher return columns do not match the expected result shape.", [], [], []);
            }

            try
            {
                await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
                var retrySql = BuildCypherSql(request.GraphName, rewritten, "result agtype");
                await using var retryCommand = new NpgsqlCommand(retrySql, connection);
                await using var retryReader = await retryCommand.ExecuteReaderAsync(cancellationToken);

                var rows = new List<Dictionary<string, string?>>();
                var nodes = new Dictionary<string, GraphNodeDto>(StringComparer.Ordinal);
                var edges = new List<GraphEdgeDto>();

                while (await retryReader.ReadAsync(cancellationToken))
                {
                    var row = new Dictionary<string, string?>(StringComparer.Ordinal);
                    for (var i = 0; i < retryReader.FieldCount; i++)
                    {
                        var key = retryReader.GetName(i);
                        var value = GetFieldValueAsString(retryReader, i);
                        row[key] = value;
                        AddGraphPrimitives(value, nodes, edges);
                    }
                    rows.Add(row);
                }

                if (nodes.Count == 0)
                {
                    var fallback = await TryBuildFallbackGraphAsync(connection, cancellationToken);
                    if (fallback.Nodes.Count > 0)
                    {
                        return new GraphQueryResponse(true, null, rows, fallback.Nodes, fallback.Edges);
                    }
                }

                if (nodes.Count == 0 && rows.Count > 0)
                {
                    var sample = string.Join(" | ", rows[0].Select(kv => $"{kv.Key}={Truncate(kv.Value, 200)}"));
                    logger.LogWarning("No graph primitives parsed from cypher result. Sample row: {Sample}", sample);
                }

                return new GraphQueryResponse(true, null, rows, nodes.Values.ToList(), edges);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Query execution failed.");
                return new GraphQueryResponse(false, ex.Message, [], [], []);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query execution failed.");
            if (ex is PostgresException pgEx && pgEx.SqlState == "42883" && pgEx.MessageText.Contains("cypher", StringComparison.OrdinalIgnoreCase))
            {
                return new GraphQueryResponse(false, "AGE cypher() function is not available. Ensure the age extension is installed and the init script has run.", [], [], []);
            }

            if (ex is PostgresException pgSyntax && pgSyntax.SqlState == "42601")
            {
                return new GraphQueryResponse(false, "Cypher syntax error. Ensure the graph name exists and the query matches Apache AGE syntax.", [], [], []);
            }

            return new GraphQueryResponse(false, ex.Message, [], [], []);
        }
    }

    public async Task<HybridRetrievalResponse> ExecuteHybridRetrievalAsync(HybridRetrievalRequest request, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return new HybridRetrievalResponse(false, "Database settings are not configured (ConnectionStrings:AgeDatabase or Database section).", [], [], [], [], string.Empty, "Graph -> Vector -> LLM");
        }

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return new HybridRetrievalResponse(false, "Query is required.", [], [], [], [], string.Empty, "Graph -> Vector -> LLM");
        }

        var steps = request.IncludeTrace ? new List<HybridRetrievalTraceStepDto>() : null;
        void AddStep(string name, string summary, string detail, int? durationMs = null)
        {
            if (steps is null)
            {
                return;
            }

            steps.Add(new HybridRetrievalTraceStepDto(name, summary, detail, durationMs));
        }

        AddStep("question", "User question received", request.Query);

        var detectedEntities = new List<string>();
        var fallbackEntities = new List<string>();
        var matchedEntities = new List<string>();
        string? clarificationQuestion = null;
        var rewrittenQuestion = request.Query;

        if (_foundry.IsConfigured)
        {
            var analysisTimer = Stopwatch.StartNew();
            try
            {
                var analysis = await _foundry.AnalyzeQuestionAsync(request.Query, request.ClarificationResponse, cancellationToken);
                analysisTimer.Stop();

                detectedEntities = analysis.Entities.ToList();
                clarificationQuestion = analysis.ClarificationQuestion;
                if (!string.IsNullOrWhiteSpace(analysis.RewrittenQuestion))
                {
                    rewrittenQuestion = analysis.RewrittenQuestion;
                }

                if (!string.IsNullOrWhiteSpace(request.ClarificationResponse)
                    && string.Equals(rewrittenQuestion, request.Query, StringComparison.OrdinalIgnoreCase))
                {
                    rewrittenQuestion = ReplaceAmbiguousPronouns(request.Query, request.ClarificationResponse);
                }

                AddStep(
                    "entity-llm",
                    $"LLM extracted {detectedEntities.Count} entities",
                    detectedEntities.Count == 0 ? "No entities detected by LLM." : string.Join(", ", detectedEntities),
                    (int)analysisTimer.ElapsedMilliseconds);

                AddStep(
                    "question-rewrite",
                    "LLM rephrased the question",
                    rewrittenQuestion);

                if (!string.IsNullOrWhiteSpace(request.ClarificationResponse))
                {
                    clarificationQuestion = null;
                    AddStep(
                        "clarification",
                        "Clarification response received",
                        request.ClarificationResponse);
                }
                else if (!string.IsNullOrWhiteSpace(clarificationQuestion))
                {
                    AddStep(
                        "clarification",
                        "Clarification required",
                        clarificationQuestion);
                }
            }
            catch (Exception ex)
            {
                analysisTimer.Stop();
                AddStep("entity-llm", "LLM entity extraction failed; falling back to heuristic", ex.Message, (int)analysisTimer.ElapsedMilliseconds);
            }
        }

        if (detectedEntities.Count == 0)
        {
            var detectionTimer = Stopwatch.StartNew();
            detectedEntities = DetectEntities(request.Query);
            detectionTimer.Stop();
            AddStep(
                "entity-detection",
                $"Detected {detectedEntities.Count} entities (heuristic)",
                detectedEntities.Count == 0 ? "No entities detected from the query text." : string.Join(", ", detectedEntities),
                (int)detectionTimer.ElapsedMilliseconds);
        }

        try
        {
            await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(clarificationQuestion))
            {
                if (request.ClarificationAttempts < 3)
                {
                    var traceSnapshot = steps is null ? null : new HybridRetrievalTraceDto(request.Query, steps);
                    return new HybridRetrievalResponse(
                        true,
                        null,
                        detectedEntities,
                        [],
                        [],
                        [],
                        string.Empty,
                        "Clarification required",
                        null,
                        traceSnapshot,
                        clarificationQuestion,
                        rewrittenQuestion,
                        _foundry.AnswerSystemPrompt);
                }

                AddStep(
                    "clarification",
                    "Clarification skipped after max attempts",
                    "Proceeding without additional clarification.");
            }

            var expansionSeeds = detectedEntities;
            if (detectedEntities.Count > 0)
            {
                var matchTimer = Stopwatch.StartNew();
                matchedEntities = await MatchEntitiesAsync(connection, detectedEntities, cancellationToken);
                matchTimer.Stop();
                if (matchedEntities.Count > 0)
                {
                    expansionSeeds = matchedEntities;
                }

                AddStep(
                    "entity-match",
                    $"Matched {matchedEntities.Count} entities in the database",
                    matchedEntities.Count == 0 ? "No entity matches found; using LLM/heuristic entities." : string.Join(", ", matchedEntities),
                    (int)matchTimer.ElapsedMilliseconds);
            }

            if (detectedEntities.Count == 0)
            {
                var fallbackTimer = Stopwatch.StartNew();
                fallbackEntities = await LoadFallbackEntitiesAsync(connection, cancellationToken);
                fallbackTimer.Stop();
                AddStep(
                    "entity-fallback",
                    $"Loaded {fallbackEntities.Count} entities from the database",
                    fallbackEntities.Count == 0 ? "No entities found in the database." : string.Join(", ", fallbackEntities),
                    (int)fallbackTimer.ElapsedMilliseconds);
                expansionSeeds = fallbackEntities;
            }

            List<string> graphEntities;
            try
            {
                var expansionTimer = Stopwatch.StartNew();
                graphEntities = await ExpandEntitiesByGraphAsync(connection, request.GraphName, expansionSeeds, request.MaxHops, cancellationToken);
                expansionTimer.Stop();

                var expansionQueries = BuildGraphExpansionQueries(expansionSeeds, request.MaxHops);
                AddStep(
                    "graph-expansion",
                    $"Expanded to {graphEntities.Count} graph entities (maxHops={request.MaxHops})",
                    expansionQueries.Count == 0 ? "No expansion queries generated." : string.Join(Environment.NewLine, expansionQueries),
                    (int)expansionTimer.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is PostgresException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Graph expansion failed; falling back to detected entities only.");
                graphEntities = detectedEntities;
                AddStep(
                    "graph-expansion",
                    "Graph expansion failed; using detected entities only",
                    ex.Message);
            }

            var entityFilter = graphEntities.Count > 0
                ? graphEntities
                : (expansionSeeds.Count > 0 ? expansionSeeds : detectedEntities);
            var embeddingTimer = Stopwatch.StartNew();
            var usedProvidedEmbedding = request.QueryEmbedding is { Length: > 0 };
            var vector = usedProvidedEmbedding
                ? request.QueryEmbedding!
                : await _foundry.CreateEmbeddingAsync(rewrittenQuestion, cancellationToken);
            embeddingTimer.Stop();
            AddStep(
                "embedding",
                usedProvidedEmbedding ? "Used provided query embedding" : "Generated query embedding",
                $"Embedding length: {vector.Length}",
                (int)embeddingTimer.ElapsedMilliseconds);

            var vectorTimer = Stopwatch.StartNew();
            var chunks = entityFilter.Count > 0
                ? await QueryChunksByEntitiesAndVectorAsync(connection, entityFilter, vector, request.Limit, cancellationToken)
                : [];
            vectorTimer.Stop();
            AddStep(
                "vector-search",
                $"Vector search returned {chunks.Count} chunks (limit={request.Limit})",
                BuildVectorSearchDetail(entityFilter),
                (int)vectorTimer.ElapsedMilliseconds);

            var prompt = BuildPromptContext(entityFilter, chunks);
            AddStep(
                "prompt-context",
                $"Prompt context built from {chunks.Count} chunks",
                prompt);

            string? answer = null;
            if (request.IncludeAnswer)
            {
                var answerTimer = Stopwatch.StartNew();
                try
                {
                    answer = await _foundry.AnswerQuestionAsync(rewrittenQuestion, prompt, cancellationToken);
                    answerTimer.Stop();
                    AddStep("llm-answer", "LLM answer generated", answer, (int)answerTimer.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    answerTimer.Stop();
                    AddStep("llm-answer", "LLM answer unavailable", ex.Message, (int)answerTimer.ElapsedMilliseconds);
                }
            }

            var traceResult = steps is null ? null : new HybridRetrievalTraceDto(request.Query, steps);
            return new HybridRetrievalResponse(
                true,
                null,
                detectedEntities,
                graphEntities,
                matchedEntities,
                chunks,
                prompt,
                "Graph -> Vector -> LLM",
                answer,
                traceResult,
                null,
                rewrittenQuestion,
                _foundry.AnswerSystemPrompt);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hybrid retrieval pipeline failed.");
            var traceFailure = steps is null ? null : new HybridRetrievalTraceDto(request.Query, steps);
            return new HybridRetrievalResponse(false, ex.Message, detectedEntities, [], [], [], string.Empty, "Graph -> Vector -> LLM", null, traceFailure, null, rewrittenQuestion, _foundry.AnswerSystemPrompt);
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

    private static List<string> BuildGraphExpansionQueries(IReadOnlyCollection<string> seedEntities, int hops)
    {
        var maxHops = Math.Clamp(hops, 1, MaxTraversalHops);
        var queries = new List<string>();

        foreach (var seed in seedEntities)
        {
            if (!Regex.IsMatch(seed, @"^[a-zA-Z0-9\s._-]{1,80}$"))
            {
                continue;
            }

            queries.Add($"MATCH (a)-[*1..{maxHops}]-(b) WHERE a.name = \"{EscapeCypherLiteral(seed)}\" RETURN DISTINCT b.name AS name LIMIT 50");
        }

        return queries;
    }

    private static string ReplaceAmbiguousPronouns(string question, string clarification)
    {
        if (string.IsNullOrWhiteSpace(clarification))
        {
            return question;
        }

        var replacement = clarification.Trim();
        var result = Regex.Replace(question, "\\b(them|they|their|it|this|that|these|those)\\b", replacement, RegexOptions.IgnoreCase);
        return result;
    }

    private static string BuildVectorSearchDetail(IReadOnlyCollection<string> entityFilter)
    {
        const string sql = "SELECT c.id, c.document_id, c.chunk_index, c.content, c.embedding <=> CAST(@query_vector AS vector) AS distance\n" +
                           "FROM chunks c\n" +
                           "JOIN chunk_entities ce ON ce.chunk_id = c.id\n" +
                           "JOIN entities e ON e.id = ce.entity_id\n" +
                           "WHERE e.name = ANY(@entity_names)\n" +
                           "  AND c.embedding IS NOT NULL\n" +
                           "ORDER BY c.embedding <=> CAST(@query_vector AS vector)\n" +
                           "LIMIT @limit;";

        var entitiesPreview = entityFilter.Count == 0
            ? "(none)"
            : string.Join(", ", entityFilter.Take(8));
        return $"SQL:\n{sql}\n\nEntity filter ({entityFilter.Count}): {entitiesPreview}";
    }

    private static async Task<List<string>> MatchEntitiesAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<string> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var lowered = candidates.Select(c => c.ToLowerInvariant()).ToArray();
        var patterns = candidates.Select(c => $"%{c}%").ToArray();
        const string sql = """
                           SELECT name
                           FROM entities
                           WHERE lower(name) = ANY(@exact)
                              OR name ILIKE ANY(@patterns)
                           ORDER BY CASE WHEN lower(name) = ANY(@exact) THEN 0 ELSE 1 END, name
                           LIMIT 20;
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("exact", lowered);
        command.Parameters.AddWithValue("patterns", patterns);

        var matches = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                var name = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    matches.Add(name);
                }
            }
        }

        return matches;
    }

    private static async Task<List<string>> LoadFallbackEntitiesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = "SELECT name FROM entities ORDER BY id DESC LIMIT 8;";
        await using var command = new NpgsqlCommand(sql, connection);
        var entities = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                var name = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    entities.Add(name);
                }
            }
        }

        return entities;
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

            var cypher = $"MATCH (a)-[*1..{maxHops}]-(b) WHERE a.name = \"{EscapeCypherLiteral(seed)}\" RETURN DISTINCT b.name AS name LIMIT 50";
            var sql = BuildCypherSql(graphName, cypher, "name agtype");

            await using var command = new NpgsqlCommand(sql, connection);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                var value = reader.GetString(0);
                var name = NormalizeAgtypeString(value);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    expanded.Add(name);
                }
            }

            await reader.CloseAsync();
        }

        return expanded.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string BuildCypherSql(string graphName, string cypherQuery, string? columnDefinition = null)
    {
        var safeGraph = EscapeSqlLiteral(graphName);
        var tag = "cypher";
        while (cypherQuery.Contains($"${tag}$", StringComparison.Ordinal))
        {
            tag = $"cypher_{Guid.NewGuid():N}";
        }

        var columns = string.IsNullOrWhiteSpace(columnDefinition)
            ? BuildColumnDefinition(cypherQuery)
            : columnDefinition;
        var columnNames = ExtractColumnNames(columns);
        var selectList = columnNames.Count == 0
            ? "*"
            : string.Join(", ", columnNames.Select(name => $"{name}::text AS {name}"));

        return $"SELECT {selectList} FROM ag_catalog.cypher('{safeGraph}', ${tag}$ {cypherQuery} ${tag}$) AS ({columns});";
    }

    private static List<string> ExtractColumnNames(string columnDefinition)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(columnDefinition))
        {
            return names;
        }

        var parts = columnDefinition.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length > 0)
            {
                names.Add(tokens[0]);
            }
        }

        return names;
    }

    private static string BuildColumnDefinition(string cypherQuery)
    {
        var returnMatch = Regex.Matches(cypherQuery, @"\bRETURN\b", RegexOptions.IgnoreCase);
        if (returnMatch.Count == 0)
        {
            return "result agtype";
        }

        var lastReturn = returnMatch[^1];
        var body = cypherQuery[(lastReturn.Index + lastReturn.Length)..];
        body = Regex.Replace(body, @"^\s*DISTINCT\s+", string.Empty, RegexOptions.IgnoreCase);

        var cutoff = Regex.Match(body, @"\bORDER\s+BY\b|\bLIMIT\b|\bSKIP\b", RegexOptions.IgnoreCase);
        if (cutoff.Success)
        {
            body = body[..cutoff.Index];
        }

        var items = SplitReturnItems(body);
        if (items.Count <= 1)
        {
            return "result agtype";
        }

        var columns = Enumerable.Range(1, items.Count).Select(i => $"col{i} agtype");
        return string.Join(", ", columns);
    }

    private static List<string> SplitReturnItems(string body)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var inSingleQuotes = false;
        var inDoubleQuotes = false;

        foreach (var ch in body)
        {
            if (ch == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
            }
            else if (ch == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
            }

            if (!inSingleQuotes && !inDoubleQuotes)
            {
                if (ch == '(' || ch == '[' || ch == '{') depth++;
                if (ch == ')' || ch == ']' || ch == '}') depth = Math.Max(0, depth - 1);
            }

            if (ch == ',' && depth == 0 && !inSingleQuotes && !inDoubleQuotes)
            {
                var item = current.ToString().Trim();
                if (item.Length > 0)
                {
                    items.Add(item);
                }
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        var last = current.ToString().Trim();
        if (last.Length > 0)
        {
            items.Add(last);
        }

        return items;
    }

    private static (string Query, string ColumnDefinition) NormalizeCypherQuery(string cypherQuery)
    {
        var columnDefinition = BuildColumnDefinition(cypherQuery);
        if (!columnDefinition.Contains(',', StringComparison.Ordinal))
        {
            return (cypherQuery, columnDefinition);
        }

        if (!TryRewriteReturnAsMap(cypherQuery, out var rewritten))
        {
            return (cypherQuery, columnDefinition);
        }

        return (rewritten, "result agtype");
    }

    private static bool TryRewriteReturnAsMap(string cypherQuery, out string rewritten)
    {
        rewritten = cypherQuery;
        var match = Regex.Match(cypherQuery, @"\bRETURN\b(?<body>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return false;
        }

        var prefix = cypherQuery[..match.Index];
        var body = match.Groups["body"].Value;
        var tailMatch = Regex.Match(body, @"\bORDER\s+BY\b|\bLIMIT\b|\bSKIP\b", RegexOptions.IgnoreCase);
        var returnList = tailMatch.Success ? body[..tailMatch.Index] : body;
        var tail = tailMatch.Success ? body[tailMatch.Index..] : string.Empty;

        returnList = Regex.Replace(returnList, @"^\s*DISTINCT\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
        var items = SplitReturnItems(returnList);
        if (items.Count <= 1)
        {
            return false;
        }

        var entries = new List<string>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i].Trim();
            var aliasMatch = Regex.Match(item, @"\bAS\s+(?<alias>[A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.IgnoreCase);
            var expr = aliasMatch.Success ? item[..aliasMatch.Index].Trim() : item;
            var key = aliasMatch.Success
                ? aliasMatch.Groups["alias"].Value
                : (Regex.IsMatch(expr, @"^[A-Za-z_][A-Za-z0-9_]*$") ? expr : $"col{i + 1}");
            entries.Add($"{key}: {expr}");
        }

        var mapLiteral = "{" + string.Join(", ", entries) + "}";
        var tailSuffix = string.IsNullOrWhiteSpace(tail) ? string.Empty : " " + tail.TrimStart();
        rewritten = $"{prefix}RETURN {mapLiteral} AS result{tailSuffix}";
        return true;
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

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

    private static string NormalizeAgtypeString(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1]
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return trimmed;
    }

    private static void AddGraphPrimitives(string? agTypeValue, IDictionary<string, GraphNodeDto> nodes, ICollection<GraphEdgeDto> edges)
    {
        if (string.IsNullOrWhiteSpace(agTypeValue))
        {
            return;
        }

        var parsed = false;
        parsed |= ExtractGraphObjects(agTypeValue, "::vertex", value => ParseVertex(value, nodes));
        parsed |= ExtractGraphObjects(agTypeValue, "::edge", value => ParseEdge(value, edges));

        if (!parsed)
        {
            if (agTypeValue.Contains("::vertex", StringComparison.OrdinalIgnoreCase))
            {
                ParseVertex(agTypeValue, nodes);
                parsed = true;
            }
            else if (agTypeValue.Contains("::edge", StringComparison.OrdinalIgnoreCase))
            {
                ParseEdge(agTypeValue, edges);
                parsed = true;
            }
        }

        if (!parsed)
        {
            _ = TryParseJsonGraphObjects(agTypeValue, nodes, edges);
        }
    }

    private static bool ExtractGraphObjects(string input, string suffix, Action<string> handler)
    {
        var found = false;
        var stack = new Stack<int>();

        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (ch == '{')
            {
                stack.Push(i);
            }
            else if (ch == '}' && stack.Count > 0)
            {
                var start = stack.Pop();
                var end = i + 1;
                var next = end;
                while (next < input.Length && char.IsWhiteSpace(input[next]))
                {
                    next++;
                }

                if (next + suffix.Length <= input.Length
                    && string.Equals(input.Substring(next, suffix.Length), suffix, StringComparison.OrdinalIgnoreCase))
                {
                    handler(input[start..(next + suffix.Length)]);
                    found = true;
                }
            }
        }

        return found;
    }

    private static bool TryParseJsonGraphObjects(string value, IDictionary<string, GraphNodeDto> nodes, ICollection<GraphEdgeDto> edges)
    {
        try
        {
            var trimmed = value.Trim();
            if (trimmed.EndsWith("::map", StringComparison.Ordinal))
            {
                trimmed = trimmed[..^"::map".Length].TrimEnd();
            }
            else if (trimmed.EndsWith("::list", StringComparison.Ordinal))
            {
                trimmed = trimmed[..^"::list".Length].TrimEnd();
            }

            trimmed = Regex.Replace(trimmed, @"::(vertex|edge)\b", string.Empty, RegexOptions.IgnoreCase);

            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                var inner = root.GetString();
                if (!string.IsNullOrWhiteSpace(inner) && !string.Equals(inner, trimmed, StringComparison.Ordinal))
                {
                    return TryParseJsonGraphObjects(inner, nodes, edges);
                }
            }

            return ExtractGraphFromElement(root, nodes, edges);
        }
        catch
        {
            return false;
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private static string? GetFieldValueAsString(NpgsqlDataReader reader, int index)
    {
        if (reader.IsDBNull(index))
        {
            return null;
        }

        var value = reader.GetValue(index);
        return value switch
        {
            string text => text,
            JsonDocument doc => doc.RootElement.GetRawText(),
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => value.ToString()
        };
    }

    private static async Task<(IReadOnlyList<GraphNodeDto> Nodes, IReadOnlyList<GraphEdgeDto> Edges)> TryBuildFallbackGraphAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var nodes = new Dictionary<string, GraphNodeDto>(StringComparer.Ordinal);
        var edges = new List<GraphEdgeDto>();

        const int maxDocuments = 50;
        const int maxChunks = 200;
        const int maxEntities = 200;
        const int maxMentions = 400;

        await using (var docCommand = new NpgsqlCommand("SELECT id, title FROM documents ORDER BY id LIMIT @limit", connection))
        {
            docCommand.Parameters.AddWithValue("limit", maxDocuments);
            await using var reader = await docCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                var title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var nodeId = $"doc:{id}";
                nodes[nodeId] = new GraphNodeDto(nodeId, "Document", string.IsNullOrWhiteSpace(title) ? "Document" : $"Document: {title}");
            }
        }

        await using (var chunkCommand = new NpgsqlCommand("SELECT id, document_id, chunk_index FROM chunks ORDER BY document_id, chunk_index LIMIT @limit", connection))
        {
            chunkCommand.Parameters.AddWithValue("limit", maxChunks);
            await using var reader = await chunkCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                var documentId = reader.GetInt64(1);
                var chunkIndex = reader.GetInt32(2);
                var nodeId = $"chunk:{id}";
                var documentNodeId = $"doc:{documentId}";
                nodes[nodeId] = new GraphNodeDto(nodeId, "Chunk", $"Chunk {chunkIndex}");
                edges.Add(new GraphEdgeDto(documentNodeId, nodeId, "HAS_CHUNK"));
            }
        }

        await using (var entityCommand = new NpgsqlCommand("SELECT id, label, name FROM entities ORDER BY id LIMIT @limit", connection))
        {
            entityCommand.Parameters.AddWithValue("limit", maxEntities);
            await using var reader = await entityCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                var label = reader.IsDBNull(1) ? "Entity" : reader.GetString(1);
                var name = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                var nodeId = $"entity:{id}";
                nodes[nodeId] = new GraphNodeDto(nodeId, label, string.IsNullOrWhiteSpace(name) ? label : $"{label}: {name}");
            }
        }

        await using (var mentionCommand = new NpgsqlCommand("SELECT chunk_id, entity_id FROM chunk_entities ORDER BY chunk_id LIMIT @limit", connection))
        {
            mentionCommand.Parameters.AddWithValue("limit", maxMentions);
            await using var reader = await mentionCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var chunkId = reader.GetInt64(0);
                var entityId = reader.GetInt64(1);
                edges.Add(new GraphEdgeDto($"chunk:{chunkId}", $"entity:{entityId}", "MENTIONS"));
            }
        }

        return (nodes.Values.ToList(), edges);
    }

    private static bool ExtractGraphFromElement(JsonElement element, IDictionary<string, GraphNodeDto> nodes, ICollection<GraphEdgeDto> edges)
    {
        var found = false;
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("start_id", out var start)
                && element.TryGetProperty("end_id", out var end)
                && element.TryGetProperty("label", out var edgeLabel))
            {
                var source = start.ToString();
                var target = end.ToString();
                var label = edgeLabel.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(target))
                {
                    edges.Add(new GraphEdgeDto(source, target, label));
                    found = true;
                }
            }
            else if (element.TryGetProperty("id", out var idValue)
                     && element.TryGetProperty("label", out var labelValue))
            {
                var id = idValue.ToString();
                var label = labelValue.GetString() ?? "Node";
                var title = label;
                if (element.TryGetProperty("properties", out var properties)
                    && properties.ValueKind == JsonValueKind.Object
                    && properties.TryGetProperty("name", out var nameValue))
                {
                    title = $"{label}: {nameValue.GetString()}";
                }

                if (!string.IsNullOrWhiteSpace(id))
                {
                    nodes[id] = new GraphNodeDto(id, label, title);
                    found = true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    found |= ExtractGraphFromElement(property.Value, nodes, edges);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                found |= ExtractGraphFromElement(item, nodes, edges);
            }
        }

        return found;
    }

    private static void ParseVertex(string value, IDictionary<string, GraphNodeDto> nodes)
    {
        var json = Regex.Replace(value, "::vertex", string.Empty, RegexOptions.IgnoreCase).Trim();
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
        var json = Regex.Replace(value, "::edge", string.Empty, RegexOptions.IgnoreCase).Trim();
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
