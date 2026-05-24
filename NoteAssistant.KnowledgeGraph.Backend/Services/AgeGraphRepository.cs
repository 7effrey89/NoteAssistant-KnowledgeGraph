using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Npgsql;
using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public sealed class AgeGraphRepository(ILogger<AgeGraphRepository> logger, IFoundryInferenceClient foundry, IAgeDatabaseConnectionFactory connectionFactory)
{
    private const int MaxTraversalHops = 3;
    private const int MaxVectorResultLimit = 50;
    private readonly IFoundryInferenceClient _foundry = foundry;
    private readonly IAgeDatabaseConnectionFactory _connectionFactory = connectionFactory;

    private sealed record CommunityEntityRow(long Id, string Label, string Name);

    private sealed record CommunityRelationshipRow(long SourceId, string SourceName, long TargetId, string TargetName, string Relationship, long? DocumentId, string? DocumentTitle, DateOnly? DocumentDate, int? ChunkIndex, string? Evidence);

    private sealed record CommunityBuildWork(int Index, IReadOnlyList<CommunityEntityRow> Entities, IReadOnlyList<CommunityRelationshipRow> Relationships, string Title);

    private sealed record CommunityBuildDetail(int Index, string Detail);

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

    public async Task<GraphNodeDetailsResponse> GetNodeDetailsAsync(GraphNodeDetailsRequest request, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return new GraphNodeDetailsResponse(false, "Database settings are not configured (ConnectionStrings:AgeDatabase or Database section).", request.Label, new Dictionary<string, string?>(), new Dictionary<string, string>());
        }

        try
        {
            await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
            return request.Label switch
            {
                "Document" => await GetDocumentNodeDetailsAsync(connection, request, cancellationToken),
                "Chunk" => await GetChunkNodeDetailsAsync(connection, request, cancellationToken),
                _ => await GetEntityNodeDetailsAsync(connection, request, cancellationToken)
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load graph node details for {Label} {Id}.", request.Label, request.Id);
            return new GraphNodeDetailsResponse(false, ex.Message, request.Label, new Dictionary<string, string?>(), new Dictionary<string, string>());
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
        void AddStep(string name, string summary, string detail, int? durationMs = null, LlmTokenUsage? tokenUsage = null)
        {
            if (steps is null)
            {
                return;
            }

            HybridTokenUsageDto? usageDto = tokenUsage is null
                ? null
                : new HybridTokenUsageDto(tokenUsage.PromptTokens, tokenUsage.CompletionTokens);
            steps.Add(new HybridRetrievalTraceStepDto(name, summary, detail, durationMs, usageDto));
        }

        AddStep("question", "User question received", request.Query);

        var detectedEntities = new List<string>();
        var fallbackEntities = new List<string>();
        var matchedEntities = new List<string>();
        string? clarificationQuestion = null;
        var rewrittenQuestion = request.Query;
        var analysisSystemPrompt = _foundry.IsConfigured ? _foundry.AnalysisSystemPrompt : null;
        var analysisUserPrompt = BuildAnalysisUserPrompt(request.Query, request.ClarificationResponse);

        if (_foundry.IsConfigured)
        {
            var analysisTimer = Stopwatch.StartNew();
            try
            {
                var analysis = await _foundry.AnalyzeQuestionAsync(request.Query, request.ClarificationResponse, cancellationToken);
                analysisTimer.Stop();

                detectedEntities = analysis.Entities.ToList();
                analysisSystemPrompt = analysis.SystemPrompt;
                analysisUserPrompt = string.IsNullOrWhiteSpace(analysis.UserPrompt) ? analysisUserPrompt : analysis.UserPrompt;
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
                    BuildPromptDetail(
                        analysisSystemPrompt,
                        analysisUserPrompt,
                        "Entities",
                        FormatEntityList(detectedEntities, 12)),
                    (int)analysisTimer.ElapsedMilliseconds,
                    analysis.TokenUsage);

                AddStep(
                    "question-rewrite",
                    "LLM rephrased the question",
                    BuildPromptDetailSystemOnly(analysisSystemPrompt),
                    null,
                    analysis.TokenUsage);

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
                AddStep(
                    "entity-llm",
                    "LLM entity extraction failed; falling back to heuristic",
                    BuildPromptDetail(
                        analysisSystemPrompt,
                        analysisUserPrompt,
                        "Error",
                        ex.Message),
                    (int)analysisTimer.ElapsedMilliseconds);
            }
        }

        if (detectedEntities.Count == 0)
        {
            var detectionTimer = Stopwatch.StartNew();
            var detection = DetectEntitiesWithDetail(request.Query);
            detectedEntities = detection.Entities;
            detectionTimer.Stop();
            AddStep(
                "entity-detection",
                $"Detected {detectedEntities.Count} entities (heuristic)",
                detection.Detail,
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
                        _foundry.AnswerSystemPrompt,
                        analysisSystemPrompt);
                }

                AddStep(
                    "clarification",
                    "Clarification skipped after max attempts",
                    "Proceeding without additional clarification.");
            }

            var queryEmbeddingForEntityMatch = request.QueryEmbedding is { Length: > 0 }
                ? request.QueryEmbedding!
                : (_foundry.IsConfigured ? await _foundry.CreateEmbeddingAsync(rewrittenQuestion, cancellationToken) : []);

            var expansionSeeds = detectedEntities;
            if (detectedEntities.Count > 0)
            {
                var matchTimer = Stopwatch.StartNew();
                var matchResult = await MatchEntitiesAsync(connection, detectedEntities, queryEmbeddingForEntityMatch, cancellationToken);
                matchedEntities = matchResult.Matched;
                var matchCandidates = matchResult.ExpandedCandidates;
                matchTimer.Stop();
                if (matchedEntities.Count > 0)
                {
                    expansionSeeds = matchedEntities;
                }

                AddStep(
                    "entity-match",
                    $"Matched {matchedEntities.Count} entities in the database",
                    BuildEntityMatchDetail(detectedEntities, matchCandidates, matchedEntities),
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

            var relationshipTimer = Stopwatch.StartNew();
            var graphRelationships = entityFilter.Count > 0
                ? await QueryGraphRelationshipsAsync(connection, entityFilter, request.Limit * 3, cancellationToken)
                : [];
            relationshipTimer.Stop();
            AddStep(
                "graph-relationships",
                $"Loaded {graphRelationships.Count} relationship triples with source evidence",
                BuildGraphRelationshipDetail(entityFilter, graphRelationships),
                (int)relationshipTimer.ElapsedMilliseconds);

            var embeddingTimer = Stopwatch.StartNew();
            var usedProvidedEmbedding = request.QueryEmbedding is { Length: > 0 };
            var vector = usedProvidedEmbedding
                ? request.QueryEmbedding!
                : (queryEmbeddingForEntityMatch.Length > 0 ? queryEmbeddingForEntityMatch : await _foundry.CreateEmbeddingAsync(rewrittenQuestion, cancellationToken));
            embeddingTimer.Stop();
            var vectorLiteral = ToVectorLiteral(vector, 1536);
            var embeddingOutput = $"Vector:\n{vectorLiteral}\nEmbedding length: {vector.Length.ToString(CultureInfo.InvariantCulture)}";
            var embeddingDetail = usedProvidedEmbedding
                ? BuildPromptDetail("(not applicable for embeddings)", "(embedding provided; no model call)", "Output", embeddingOutput)
                : BuildPromptDetail(
                    "(not applicable for embeddings)",
                    rewrittenQuestion,
                    "Output",
                    embeddingOutput);
            AddStep(
                "embedding",
                usedProvidedEmbedding ? "Used provided query embedding" : "Generated query embedding",
                embeddingDetail,
                (int)embeddingTimer.ElapsedMilliseconds);

            var vectorTimer = Stopwatch.StartNew();
            var chunks = entityFilter.Count > 0
                ? await QueryChunksByEntitiesHybridAsync(connection, entityFilter, rewrittenQuestion, vector, request.Limit, cancellationToken)
                : [];
            vectorTimer.Stop();
            AddStep(
                "vector-search",
                $"Hybrid chunk retrieval returned {chunks.Count} chunks (vector + keyword RRF, limit={request.Limit})",
                BuildHybridChunkSearchDetail(entityFilter, rewrittenQuestion, request.Limit, vector),
                (int)vectorTimer.ElapsedMilliseconds);

            var prompt = BuildPromptContext(entityFilter, graphRelationships, chunks);
            AddStep(
                "prompt-context",
                $"Prompt context built from {graphRelationships.Count} graph triples and {chunks.Count} chunks",
                prompt);

            string? answer = null;
            if (request.IncludeAnswer)
            {
                var answerTimer = Stopwatch.StartNew();
                try
                {
                    var answerResult = await _foundry.AnswerQuestionAsync(rewrittenQuestion, prompt, cancellationToken);
                    answer = answerResult.Answer;
                    answerTimer.Stop();
                    var answerPrompt = BuildAnswerUserPrompt(prompt, rewrittenQuestion);
                    AddStep(
                        "llm-answer",
                        "LLM answer generated",
                        BuildPromptDetailPromptsOnly(_foundry.AnswerSystemPrompt, answerPrompt),
                        (int)answerTimer.ElapsedMilliseconds,
                        answerResult.TokenUsage);
                }
                catch (Exception)
                {
                    answerTimer.Stop();
                    var answerPrompt = BuildAnswerUserPrompt(prompt, rewrittenQuestion);
                    AddStep(
                        "llm-answer",
                        "LLM answer unavailable",
                        BuildPromptDetailPromptsOnly(_foundry.AnswerSystemPrompt, answerPrompt),
                        (int)answerTimer.ElapsedMilliseconds);
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
                _foundry.AnswerSystemPrompt,
                analysisSystemPrompt,
                graphRelationships);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hybrid retrieval pipeline failed.");
            var traceFailure = steps is null ? null : new HybridRetrievalTraceDto(request.Query, steps);
            return new HybridRetrievalResponse(false, ex.Message, detectedEntities, [], [], [], string.Empty, "Graph -> Vector -> LLM", null, traceFailure, null, rewrittenQuestion, _foundry.AnswerSystemPrompt, analysisSystemPrompt);
        }
    }

    public async Task<CommunityBuildResponse> BuildCommunitiesAsync(
        bool includeTrace,
        CancellationToken cancellationToken,
        Func<HybridRetrievalTraceStepDto, CancellationToken, Task>? progressCallback = null,
        Func<string, string, CancellationToken, Task>? progressStartedCallback = null,
        int maxParallelism = 1)
    {
        if (!IsConfigured)
        {
            return new CommunityBuildResponse(false, "Database settings are not configured.", 0, 0, 0);
        }

        if (!_foundry.IsConfigured)
        {
            return new CommunityBuildResponse(false, "Foundry inference is not configured.", 0, 0, 0);
        }

        var steps = includeTrace ? new List<HybridRetrievalTraceStepDto>() : null;
        var stepsLock = new object();
        async Task AddStepAsync(string name, string summary, string detail, int? durationMs = null, LlmTokenUsage? tokenUsage = null)
        {
            HybridTokenUsageDto? usageDto = tokenUsage is null
                ? null
                : new HybridTokenUsageDto(tokenUsage.PromptTokens, tokenUsage.CompletionTokens);
            var step = new HybridRetrievalTraceStepDto(name, summary, detail, durationMs, usageDto);
            lock (stepsLock)
            {
                steps?.Add(step);
            }
            if (progressCallback is not null)
            {
                await progressCallback(step, cancellationToken);
            }
        }

        async Task StartStepAsync(string name, string summary)
        {
            if (progressStartedCallback is not null)
            {
                await progressStartedCallback(name, summary, cancellationToken);
            }
        }

        try
        {
            await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
            await StartStepAsync("community-schema", "Ensuring community tables");
            var setupTimer = Stopwatch.StartNew();
            await EnsureCommunityTablesAsync(connection, cancellationToken);
            setupTimer.Stop();
            await AddStepAsync("community-schema", "Community tables ensured", BuildCommunitySchemaDetail(), (int)setupTimer.ElapsedMilliseconds);

            await StartStepAsync("community-load", "Loading entities and relationships");
            var loadTimer = Stopwatch.StartNew();
            var entities = await LoadCommunityEntitiesAsync(connection, cancellationToken);
            var relationships = await LoadCommunityRelationshipsAsync(connection, cancellationToken);
            loadTimer.Stop();
            await AddStepAsync("community-load", $"Loaded {entities.Count} entities and {relationships.Count} relationships", BuildCommunityLoadDetail(entities, relationships), (int)loadTimer.ElapsedMilliseconds);

            if (entities.Count == 0 || relationships.Count == 0)
            {
                var emptyTrace = steps is null ? null : new HybridRetrievalTraceDto("Build communities", steps);
                return new CommunityBuildResponse(true, null, 0, 0, relationships.Count, emptyTrace);
            }

            await StartStepAsync("community-clustering", "Detecting graph communities");
            var components = LeidenCommunityDetector.DetectCommunities(
                entities.Select(entity => entity.Id).ToList(),
                relationships.Select(relationship => (relationship.SourceId, relationship.TargetId)).ToList());
            await AddStepAsync("community-clustering", $"Identified {components.Count} graph communities with Leiden optimization", BuildCommunityClusteringDetail(components, entities, relationships));
            var buildTimer = Stopwatch.StartNew();
            await StartStepAsync("community-reset", "Clearing existing community index");
            await ClearCommunityTablesAsync(connection, cancellationToken);
            await AddStepAsync("community-reset", "Existing community index cleared", "SQL:\nTRUNCATE TABLE community_sources, community_members, communities RESTART IDENTITY;");

            var built = 0;
            var assigned = 0;
            var summaryDetails = new ConcurrentBag<CommunityBuildDetail>();
            var selectedComponents = components.Where(component => component.Count > 1).OrderByDescending(component => component.Count).Take(50).ToList();
            var workItems = selectedComponents
                .Select((component, index) =>
                {
                    var componentRelationships = relationships
                        .Where(relationship => component.Contains(relationship.SourceId) && component.Contains(relationship.TargetId))
                        .ToList();
                    var componentEntities = entities.Where(entity => component.Contains(entity.Id)).ToList();
                    return new CommunityBuildWork(index + 1, componentEntities, componentRelationships, BuildCommunityTitle(componentEntities));
                })
                .Where(work => work.Relationships.Count > 0)
                .ToList();
            var communityParallelism = Math.Clamp(maxParallelism, 1, 8);
            await Parallel.ForEachAsync(workItems, new ParallelOptions { MaxDegreeOfParallelism = communityParallelism, CancellationToken = cancellationToken }, async (work, token) =>
            {
                await StartStepAsync("community-progress", $"Building community {work.Index} of {workItems.Count}: {work.Title}");
                var context = BuildCommunitySummaryContext(work.Entities, work.Relationships);
                const string summaryQuestion = "Summarize this graph community. Include the main topic, key entities, relationship patterns, and any temporal span.";
                var summaryResult = await _foundry.AnswerQuestionAsync(summaryQuestion, context, token, "Community Summary Agent");
                var summary = string.IsNullOrWhiteSpace(summaryResult.Answer) ? context : summaryResult.Answer.Trim();
                var embedding = await _foundry.CreateEmbeddingAsync(summary, token);
                var key = ComputeCommunityKey(work.Entities);
                await using var writeConnection = await _connectionFactory.OpenAsync(token);
                var communityId = await UpsertCommunityAsync(writeConnection, key, work.Title, summary, embedding, work.Entities.Count, work.Relationships.Count, work.Relationships, token);
                await InsertCommunityMembersAsync(writeConnection, communityId, work.Entities, token);
                await InsertCommunitySourcesAsync(writeConnection, communityId, work.Relationships, token);
                var summaryDetail = BuildCommunitySummaryTraceDetail(communityId, key, work.Title, summaryQuestion, context, summary, work.Entities, work.Relationships);
                summaryDetails.Add(new CommunityBuildDetail(work.Index, summaryDetail));
                var completed = Interlocked.Increment(ref built);
                Interlocked.Add(ref assigned, work.Entities.Count);
                await AddStepAsync(
                    "community-progress",
                    $"Built community {work.Index} of {workItems.Count}: {work.Title}",
                    summaryDetail,
                    null,
                    summaryResult.TokenUsage);
            });

            buildTimer.Stop();
            await StartStepAsync("community-summary", "Finalizing community build summary");
            var orderedSummaryDetails = summaryDetails.OrderBy(detail => detail.Index).Select(detail => detail.Detail).ToList();
            await AddStepAsync("community-summary", $"Built {built} community summaries", BuildCommunitySummaryBuildDetail(assigned, orderedSummaryDetails), (int)buildTimer.ElapsedMilliseconds);
            var trace = steps is null ? null : new HybridRetrievalTraceDto("Build communities", steps);
            return new CommunityBuildResponse(true, null, built, assigned, relationships.Count, trace);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Community build failed.");
            var trace = steps is null ? null : new HybridRetrievalTraceDto("Build communities", steps);
            return new CommunityBuildResponse(false, ex.Message, 0, 0, 0, trace);
        }
    }

    public async Task<GlobalGraphRagResponse> ExecuteGlobalGraphRagAsync(GlobalGraphRagRequest request, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return new GlobalGraphRagResponse(false, "Database settings are not configured.", [], [], string.Empty, "Global Community Summaries -> Temporal Timeline -> LLM");
        }

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return new GlobalGraphRagResponse(false, "Query is required.", [], [], string.Empty, "Global Community Summaries -> Temporal Timeline -> LLM");
        }

        var steps = request.IncludeTrace ? new List<HybridRetrievalTraceStepDto>() : null;
        void AddStep(string name, string summary, string detail, int? durationMs = null, LlmTokenUsage? tokenUsage = null)
        {
            HybridTokenUsageDto? usageDto = tokenUsage is null
                ? null
                : new HybridTokenUsageDto(tokenUsage.PromptTokens, tokenUsage.CompletionTokens);
            steps?.Add(new HybridRetrievalTraceStepDto(name, summary, detail, durationMs, usageDto));
        }

        try
        {
            await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
            await EnsureCommunityTablesAsync(connection, cancellationToken);
            AddStep("global-question", "Global question received", request.Query);

            var embeddingTimer = Stopwatch.StartNew();
            var vector = request.QueryEmbedding is { Length: > 0 }
                ? request.QueryEmbedding!
                : await _foundry.CreateEmbeddingAsync(request.Query, cancellationToken);
            embeddingTimer.Stop();
            AddStep("global-embedding", "Generated global query embedding", $"Embedding length: {vector.Length.ToString(CultureInfo.InvariantCulture)}", (int)embeddingTimer.ElapsedMilliseconds);

            var retrievalTimer = Stopwatch.StartNew();
            var communities = await QueryRelevantCommunitiesAsync(connection, vector, request.Limit, cancellationToken);
            retrievalTimer.Stop();
            AddStep("community-retrieval", $"Retrieved {communities.Count} community summaries", BuildCommunityRetrievalDetail(communities), (int)retrievalTimer.ElapsedMilliseconds);

            var timelineTimer = Stopwatch.StartNew();
            var timeline = await QueryTemporalTimelineAsync(connection, request.Query, 20, cancellationToken);
            timelineTimer.Stop();
            AddStep("temporal-context", $"Loaded {timeline.Count} dated documents for temporal context", BuildTimelineDetail(timeline), (int)timelineTimer.ElapsedMilliseconds);

            var prompt = BuildGlobalPromptContext(communities, timeline);
            AddStep("global-prompt-context", "Built global prompt context", prompt);

            string? answer = null;
            if (request.IncludeAnswer)
            {
                var answerTimer = Stopwatch.StartNew();
                var answerResult = await _foundry.AnswerQuestionAsync(request.Query, prompt, cancellationToken, "Global Answer Agent");
                answer = answerResult.Answer;
                answerTimer.Stop();
                AddStep("global-answer", "Generated global GraphRAG answer", BuildPromptDetailPromptsOnly(_foundry.AnswerSystemPrompt, BuildAnswerUserPrompt(prompt, request.Query)), (int)answerTimer.ElapsedMilliseconds, answerResult.TokenUsage);
            }

            var trace = steps is null ? null : new HybridRetrievalTraceDto(request.Query, steps);
            return new GlobalGraphRagResponse(true, null, communities, timeline, prompt, "Global Community Summaries -> Temporal Timeline -> LLM", answer, trace);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Global GraphRAG failed.");
            var trace = steps is null ? null : new HybridRetrievalTraceDto(request.Query, steps);
            return new GlobalGraphRagResponse(false, ex.Message, [], [], string.Empty, "Global Community Summaries -> Temporal Timeline -> LLM", null, trace);
        }
    }

    private static async Task EnsureCommunityTablesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var sql = CommunitySchemaSql();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string CommunitySchemaSql()
        => """
           CREATE TABLE IF NOT EXISTS communities (
               id BIGSERIAL PRIMARY KEY,
               community_key TEXT NOT NULL UNIQUE,
               title TEXT NOT NULL,
               summary TEXT NOT NULL,
               embedding vector(1536),
               entity_count INTEGER NOT NULL DEFAULT 0,
               relationship_count INTEGER NOT NULL DEFAULT 0,
               start_date DATE NULL,
               end_date DATE NULL,
               created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
               updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
           );
           CREATE TABLE IF NOT EXISTS community_members (
               community_id BIGINT NOT NULL REFERENCES communities(id) ON DELETE CASCADE,
               entity_id BIGINT NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
               PRIMARY KEY (community_id, entity_id)
           );
           CREATE TABLE IF NOT EXISTS community_sources (
               community_id BIGINT NOT NULL REFERENCES communities(id) ON DELETE CASCADE,
               document_id BIGINT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
               PRIMARY KEY (community_id, document_id)
           );
           DO $$ BEGIN CREATE INDEX IF NOT EXISTS idx_communities_embedding ON communities USING diskann (embedding vector_cosine_ops); EXCEPTION WHEN OTHERS THEN RAISE NOTICE 'community diskann index creation skipped.'; END $$;
           CREATE INDEX IF NOT EXISTS idx_community_members_entity ON community_members(entity_id);
           CREATE INDEX IF NOT EXISTS idx_community_sources_document ON community_sources(document_id);
           """;

    private static string BuildCommunitySchemaDetail()
        => string.Join(Environment.NewLine,
        [
            "Purpose: materialize a reusable global GraphRAG index over graph communities.",
            string.Empty,
            "Tables:",
            "- communities: one row per detected graph component/community, with summary text and embedding.",
            "- community_members: many-to-many mapping from community to entities.",
            "- community_sources: documents that contributed evidence to the community.",
            string.Empty,
            "SQL:",
            CommunitySchemaSql()
        ]);

    private static async Task ClearCommunityTablesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = "TRUNCATE TABLE community_sources, community_members, communities RESTART IDENTITY;";
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<CommunityEntityRow>> LoadCommunityEntitiesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var sql = CommunityEntityLoadSql();
        await using var command = new NpgsqlCommand(sql, connection);
        var entities = new List<CommunityEntityRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entities.Add(new CommunityEntityRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }

        return entities;
    }

    private static string CommunityEntityLoadSql()
        => "SELECT id, label, name FROM entities ORDER BY id;";

    private static async Task<List<CommunityRelationshipRow>> LoadCommunityRelationshipsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await EnsureRelationshipsTableForReadAsync(connection, cancellationToken);
         var sql = CommunityRelationshipLoadSql();
        await using var command = new NpgsqlCommand(sql, connection);
        var relationships = new List<CommunityRelationshipRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            relationships.Add(new CommunityRelationshipRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateOnly>(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return relationships;
    }

        private static string CommunityRelationshipLoadSql()
         => """
            SELECT s.id AS source_id,
                s.name AS source_name,
                t.id AS target_id,
                t.name AS target_name,
                r.relationship_type,
                c.document_id,
                d.title,
                d.document_date,
                c.chunk_index,
                COALESCE(r.evidence, c.content) AS evidence
            FROM relationships r
            JOIN entities s ON s.id = r.source_entity_id
            JOIN entities t ON t.id = r.target_entity_id
            LEFT JOIN chunks c ON c.id = r.chunk_id
            LEFT JOIN documents d ON d.id = c.document_id
            UNION ALL
            SELECT e1.id,
                e1.name,
                e2.id,
                e2.name,
                'CO_MENTIONED_WITH',
                c.document_id,
                d.title,
                d.document_date,
                c.chunk_index,
                c.content
            FROM chunks c
            JOIN documents d ON d.id = c.document_id
            JOIN chunk_entities ce1 ON ce1.chunk_id = c.id
            JOIN entities e1 ON e1.id = ce1.entity_id
            JOIN chunk_entities ce2 ON ce2.chunk_id = c.id
            JOIN entities e2 ON e2.id = ce2.entity_id
            WHERE e1.id < e2.id;
            """;

    private static async Task EnsureRelationshipsTableForReadAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
                           CREATE TABLE IF NOT EXISTS relationships (
                               id BIGSERIAL PRIMARY KEY,
                               source_entity_id BIGINT NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
                               target_entity_id BIGINT NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
                               relationship_type TEXT NOT NULL,
                               chunk_id BIGINT NULL REFERENCES chunks(id) ON DELETE SET NULL,
                               confidence DOUBLE PRECISION NULL,
                               evidence TEXT NULL,
                               created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                               UNIQUE(source_entity_id, target_entity_id, relationship_type, chunk_id)
                           );
                           """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildCommunitySummaryContext(IReadOnlyList<CommunityEntityRow> entities, IReadOnlyList<CommunityRelationshipRow> relationships)
    {
        var entityText = string.Join(", ", entities.Take(40).Select(entity => $"{entity.Label}:{entity.Name}"));
        var relationshipText = string.Join(Environment.NewLine, relationships.Take(80).Select(r => $"- {r.SourceName} -[{r.Relationship}]-> {r.TargetName} ({r.DocumentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "undated"}; {r.DocumentTitle ?? "unknown document"})"));
        var evidenceText = string.Join(Environment.NewLine, relationships.Select(r => r.Evidence).Where(evidence => !string.IsNullOrWhiteSpace(evidence)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8));
        return $"Entities:\n{entityText}\n\nRelationships:\n{relationshipText}\n\nEvidence:\n{evidenceText}";
    }

    private static string BuildCommunityClusteringDetail(IReadOnlyList<HashSet<long>> components, IReadOnlyList<CommunityEntityRow> entities, IReadOnlyList<CommunityRelationshipRow> relationships)
    {
        var entityLookup = entities.ToDictionary(entity => entity.Id, entity => entity);
        var displayLimit = components.Count <= 50 ? components.Count : 50;
        var ranked = components
            .OrderByDescending(component => component.Count)
            .Take(displayLimit)
            .Select((component, index) =>
            {
                var names = component
                    .Select(id => entityLookup.TryGetValue(id, out var entity) ? entity.Name : id.ToString(CultureInfo.InvariantCulture))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Take(12);
                var edgeCount = relationships.Count(r => component.Contains(r.SourceId) && component.Contains(r.TargetId));
                return $"{index + 1}. entities={component.Count}, relationships={edgeCount}: {string.Join(", ", names)}";
            });

        return string.Join(Environment.NewLine,
        [
            "Algorithm: Leiden community detection (local moving with connectivity refinement) over an undirected weighted entity graph.",
            "Nodes: rows from kg_data.entities.",
            "Edges: typed kg_data.relationships plus chunk co-mentions (CO_MENTIONED_WITH fallback).",
            "Rule: maximize modularity by moving nodes into strongly connected communities, then refine disconnected assignments; summaries are generated for communities with more than one entity.",
            string.Empty,
            $"Total communities: {components.Count}",
            $"Singleton communities: {components.Count(component => component.Count == 1)}",
            $"Multi-entity communities: {components.Count(component => component.Count > 1)}",
            string.Empty,
            components.Count <= displayLimit
                ? $"Communities (all {components.Count} shown):"
                : $"Largest communities (showing {displayLimit} of {components.Count}; {components.Count - displayLimit} smaller communities omitted from this trace):",
            string.Join(Environment.NewLine, ranked)
        ]);
    }

    private static string BuildCommunitySummaryTraceDetail(long communityId, string key, string title, string question, string context, string summary, IReadOnlyList<CommunityEntityRow> entities, IReadOnlyList<CommunityRelationshipRow> relationships)
        => string.Join(Environment.NewLine,
        [
            $"Community id: {communityId}",
            $"Community key: {key}",
            $"Title: {title}",
            $"Entities: {entities.Count}",
            $"Relationships: {relationships.Count}",
            string.Empty,
            "Persisted communities row:",
            $"id={communityId}, community_key={key}, title={title}, entity_count={entities.Count}, relationship_count={relationships.Count}",
            string.Empty,
            "Persisted community_members rows (top 50):",
            string.Join(Environment.NewLine, entities.Take(50).Select(entity => $"community_id={communityId}, entity_id={entity.Id}, entity={entity.Label}:{entity.Name}")),
            string.Empty,
            "Persisted community_sources rows (top 50):",
            string.Join(Environment.NewLine, relationships.Select(r => r.DocumentId).Where(id => id.HasValue).Select(id => id!.Value).Distinct().Take(50).Select(documentId => $"community_id={communityId}, document_id={documentId}")),
            string.Empty,
            "LLM question:",
            question,
            string.Empty,
            "LLM context:",
            TruncateTraceText(context, 3500),
            string.Empty,
            "Generated summary:",
            TruncateTraceText(summary, 1800)
        ]);

    private static string BuildCommunitySummaryBuildDetail(int assigned, IReadOnlyList<string> summaryDetails)
        => string.Join(Environment.NewLine,
        [
            $"Entities assigned: {assigned}",
            string.Empty,
            "For each selected multi-entity component:",
            string.Empty,
            "1. Build LLM context from entity labels/names, relationship triples, document dates/titles, and evidence text.",
            "2. Ask Foundry to summarize the community's main topic, key entities, relationship patterns, and temporal span.",
            "3. Embed the summary and persist it to kg_data.communities.",
            "4. Persist entity membership to kg_data.community_members and source documents to kg_data.community_sources.",
            string.Empty,
            "Write SQL templates:",
            CommunityUpsertSql(),
            string.Empty,
            CommunityMemberInsertSql(),
            string.Empty,
            CommunitySourceInsertSql(),
            string.Empty,
            summaryDetails.Count == 0 ? "No community summary examples were generated." : string.Join(Environment.NewLine + Environment.NewLine + "---" + Environment.NewLine + Environment.NewLine, summaryDetails)
        ]);

    private static string CommunityUpsertSql()
        => """
           INSERT INTO communities(community_key, title, summary, embedding, entity_count, relationship_count, start_date, end_date, updated_at)
           VALUES (@key, @title, @summary, CAST(@embedding AS vector), @entity_count, @relationship_count, @start_date, @end_date, NOW())
           ON CONFLICT (community_key) DO UPDATE SET
               title = EXCLUDED.title,
               summary = EXCLUDED.summary,
               embedding = EXCLUDED.embedding,
               entity_count = EXCLUDED.entity_count,
               relationship_count = EXCLUDED.relationship_count,
               start_date = EXCLUDED.start_date,
               end_date = EXCLUDED.end_date,
               updated_at = NOW()
           RETURNING id;
           """;

    private static string CommunityMemberInsertSql()
        => "INSERT INTO community_members(community_id, entity_id) VALUES (@community_id, @entity_id) ON CONFLICT DO NOTHING;";

    private static string CommunitySourceInsertSql()
        => "INSERT INTO community_sources(community_id, document_id) VALUES (@community_id, @document_id) ON CONFLICT DO NOTHING;";

    private static string TruncateTraceText(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + $"\n... truncated {value.Length - maxLength} characters";

    private static string BuildCommunityTitle(IReadOnlyList<CommunityEntityRow> entities)
        => string.Join(" / ", entities.OrderBy(entity => entity.Label == "Tag" ? 1 : 0).ThenBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase).Take(4).Select(entity => entity.Name));

    private static string ComputeCommunityKey(IReadOnlyList<CommunityEntityRow> entities)
    {
        var source = string.Join("|", entities.Select(entity => entity.Id).OrderBy(id => id));
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private static async Task<long> UpsertCommunityAsync(NpgsqlConnection connection, string key, string title, string summary, float[] embedding, int entityCount, int relationshipCount, IReadOnlyList<CommunityRelationshipRow> relationships, CancellationToken cancellationToken)
    {
        var startDate = relationships.Where(r => r.DocumentDate.HasValue).Select(r => r.DocumentDate!.Value).DefaultIfEmpty().Min();
        var endDate = relationships.Where(r => r.DocumentDate.HasValue).Select(r => r.DocumentDate!.Value).DefaultIfEmpty().Max();
        var hasDates = relationships.Any(r => r.DocumentDate.HasValue);
        var sql = CommunityUpsertSql();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("title", string.IsNullOrWhiteSpace(title) ? "Untitled community" : title);
        command.Parameters.AddWithValue("summary", summary);
        command.Parameters.AddWithValue("embedding", ToVectorLiteral(embedding, 1536));
        command.Parameters.AddWithValue("entity_count", entityCount);
        command.Parameters.AddWithValue("relationship_count", relationshipCount);
        command.Parameters.AddWithValue("start_date", hasDates ? startDate : DBNull.Value);
        command.Parameters.AddWithValue("end_date", hasDates ? endDate : DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task InsertCommunityMembersAsync(NpgsqlConnection connection, long communityId, IReadOnlyList<CommunityEntityRow> entities, CancellationToken cancellationToken)
    {
        foreach (var entity in entities)
        {
            await using var command = new NpgsqlCommand(CommunityMemberInsertSql(), connection);
            command.Parameters.AddWithValue("community_id", communityId);
            command.Parameters.AddWithValue("entity_id", entity.Id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertCommunitySourcesAsync(NpgsqlConnection connection, long communityId, IReadOnlyList<CommunityRelationshipRow> relationships, CancellationToken cancellationToken)
    {
        foreach (var documentId in relationships.Select(r => r.DocumentId).Where(id => id.HasValue).Select(id => id!.Value).Distinct())
        {
            await using var command = new NpgsqlCommand(CommunitySourceInsertSql(), connection);
            command.Parameters.AddWithValue("community_id", communityId);
            command.Parameters.AddWithValue("document_id", documentId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<List<GlobalCommunityResultDto>> QueryRelevantCommunitiesAsync(NpgsqlConnection connection, float[] queryEmbedding, int limit, CancellationToken cancellationToken)
    {
        var constrainedLimit = Math.Clamp(limit, 1, 20);
        const string sql = """
                           SELECT id, title, summary, embedding <=> CAST(@query_vector AS vector) AS distance, entity_count, relationship_count, start_date, end_date
                           FROM communities
                           WHERE embedding IS NOT NULL
                           ORDER BY embedding <=> CAST(@query_vector AS vector)
                           LIMIT @limit;
                           """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("query_vector", ToVectorLiteral(queryEmbedding, 1536));
        command.Parameters.AddWithValue("limit", constrainedLimit);
        var communities = new List<GlobalCommunityResultDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            communities.Add(new GlobalCommunityResultDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateOnly>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateOnly>(7)));
        }

        return communities;
    }

    private static async Task<List<TemporalDocumentDto>> QueryTemporalTimelineAsync(NpgsqlConnection connection, string query, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT id, title, document_date, document_type, tags::text
                           FROM documents
                           ORDER BY document_date NULLS LAST, title
                           LIMIT 200;
                           """;
        await using var command = new NpgsqlCommand(sql, connection);
        var docs = new List<TemporalDocumentDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var title = reader.GetString(1);
            var date = reader.IsDBNull(2) ? TryParseDateFromTitle(title) : reader.GetFieldValue<DateOnly>(2);
            docs.Add(new TemporalDocumentDto(
                reader.GetInt64(0),
                title,
                date,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return docs
            .Where(doc => doc.DocumentDate.HasValue)
            .OrderBy(doc => doc.DocumentDate)
            .ThenBy(doc => doc.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 50))
            .ToList();
    }

    private static DateOnly? TryParseDateFromTitle(string title)
    {
        var match = Regex.Match(title, @"(?<day>\d{2})-(?<month>\d{2})-(?<year>\d{2})(?:\s|_|$)");
        if (!match.Success)
        {
            return null;
        }

        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture);
        var year = 2000 + int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
        try
        {
            return new DateOnly(year, month, day);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string BuildGlobalPromptContext(IReadOnlyList<GlobalCommunityResultDto> communities, IReadOnlyList<TemporalDocumentDto> timeline)
    {
        var communityText = communities.Count == 0
            ? "No community summaries found. Build communities first."
            : string.Join(Environment.NewLine + Environment.NewLine, communities.Select(c => $"Community {c.Id}: {c.Title}\nSpan: {c.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown"} to {c.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown"}\nEntities: {c.EntityCount}; Relationships: {c.RelationshipCount}\nSummary: {c.Summary}"));
        var timelineText = timeline.Count == 0
            ? "No dated documents found."
            : string.Join(Environment.NewLine, timeline.Select(d => $"- {d.DocumentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "undated"}: {d.Title} ({d.DocumentType ?? "unknown"}) {d.Tags ?? string.Empty}"));
        return $"Community summaries:\n{communityText}\n\nChronological timeline:\n{timelineText}";
    }

    private static string BuildCommunityLoadDetail(IReadOnlyList<CommunityEntityRow> entities, IReadOnlyList<CommunityRelationshipRow> relationships)
    {
        var entitySample = entities.Count == 0
            ? "(none)"
            : string.Join(Environment.NewLine, entities.Take(50).Select(entity => $"{entity.Id}: {entity.Label} - {entity.Name}"));
        var relationshipSample = relationships.Count == 0
            ? "(none)"
            : string.Join(Environment.NewLine, relationships.Take(50).Select(r => $"{r.SourceName} -[{r.Relationship}]-> {r.TargetName} ({r.DocumentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "undated"}; {r.DocumentTitle ?? "unknown document"})"));

        return relationships.Count == 0
            ? string.Join(Environment.NewLine,
            [
                "This is a READ step. It does not insert anything; it loads already-ingested graph facts that will be clustered in later steps.",
                "No relationships found.",
                string.Empty,
                "Entity SQL:",
                CommunityEntityLoadSql(),
                string.Empty,
                "Entity output sample (top 50):",
                entitySample,
                string.Empty,
                "Relationship SQL:",
                CommunityRelationshipLoadSql(),
                string.Empty,
                "Relationship output sample (top 50):",
                relationshipSample
            ])
            : string.Join(Environment.NewLine,
            [
                "This is a READ step. It does not insert anything; it loads already-ingested graph facts that will be clustered in later steps.",
                "The INSERT/UPSERT statements happen later in the community-summary step after components are identified and summarized.",
                string.Empty,
                "Entity SQL:",
                CommunityEntityLoadSql(),
                string.Empty,
                "Entity output sample (top 50):",
                entitySample,
                string.Empty,
                "Relationship SQL:",
                CommunityRelationshipLoadSql(),
                string.Empty,
                "Relationship output sample (top 50):",
                relationshipSample
            ]);
    }

    private static string BuildCommunityRetrievalDetail(IReadOnlyList<GlobalCommunityResultDto> communities)
        => communities.Count == 0 ? "No communities found. Run Build Communities first." : string.Join(Environment.NewLine, communities.Select(c => $"{c.Id}: {c.Title} distance={c.Distance?.ToString("0.####", CultureInfo.InvariantCulture) ?? "n/a"}"));

    private static string BuildTimelineDetail(IReadOnlyList<TemporalDocumentDto> timeline)
        => timeline.Count == 0 ? "No dated documents found." : string.Join(Environment.NewLine, timeline.Select(d => $"{d.DocumentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "undated"}: {d.Title}"));

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

    private static (List<string> Entities, string Detail) DetectEntitiesWithDetail(string query)
    {
        var known = new[] { "Microsoft", "OpenAI", "Azure", "PostgreSQL", "Apache AGE", "DiskANN", "pgvector" };
        var knownMatches = new List<string>();
        var regexMatches = new List<string>();
        var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in known)
        {
            if (query.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                knownMatches.Add(candidate);
                entities.Add(candidate);
            }
        }

        foreach (Match match in Regex.Matches(query, @"\b[A-Z][A-Za-z0-9]+(?:\s+[A-Z][A-Za-z0-9]+)?\b"))
        {
            var value = match.Value.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                regexMatches.Add(value);
            }
        }

        var uniqueRegex = regexMatches.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var filteredRegex = uniqueRegex
            .Where(value => value.Length is >= 3 and <= 80)
            .ToList();
        foreach (var candidate in filteredRegex)
        {
            entities.Add(candidate);
        }

        var finalEntities = entities.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        var detail = string.Join(Environment.NewLine,
        [
            $"Step 1: Known term matches ({knownMatches.Count}): {FormatEntityList(knownMatches, 12)}",
            $"Step 2: Regex candidates ({uniqueRegex.Count}): {FormatEntityList(uniqueRegex, 12)}",
            $"Step 3: Length-filtered candidates ({filteredRegex.Count}): {FormatEntityList(filteredRegex, 12)}",
            $"Step 4: Final entities (max 8): {FormatEntityList(finalEntities, 12)}"
        ]);

        return (finalEntities, detail);
    }

    private static string FormatEntityList(IReadOnlyCollection<string> items, int maxItems)
    {
        if (items.Count == 0)
        {
            return "(none)";
        }

        var preview = items.Take(maxItems).ToList();
        var text = string.Join(", ", preview);
        return items.Count > maxItems ? $"{text} (+{items.Count - maxItems} more)" : text;
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

    private static string BuildGraphRelationshipDetail(IReadOnlyCollection<string> entityFilter, IReadOnlyCollection<HybridGraphRelationshipDto> relationships)
    {
        const string parameterizedSql = "WITH typed_relationships AS (\n" +
                        "    SELECT s.name AS source, r.relationship_type AS relationship, t.name AS target, c.document_id, c.chunk_index, COALESCE(r.evidence, c.content) AS source_text, 0 AS priority\n" +
                        "    FROM relationships r\n" +
                        "    JOIN entities s ON s.id = r.source_entity_id\n" +
                        "    JOIN entities t ON t.id = r.target_entity_id\n" +
                        "    LEFT JOIN chunks c ON c.id = r.chunk_id\n" +
                        "    WHERE s.name = ANY(@entity_names) OR t.name = ANY(@entity_names)\n" +
                        "), co_mentions AS (\n" +
                        "    SELECT e1.name AS source, 'CO_MENTIONED_WITH' AS relationship, e2.name AS target, c.document_id, c.chunk_index, c.content AS source_text, 1 AS priority\n" +
                        "    FROM chunks c\n" +
                        "    JOIN chunk_entities ce1 ON ce1.chunk_id = c.id\n" +
                        "    JOIN entities e1 ON e1.id = ce1.entity_id\n" +
                        "    JOIN chunk_entities ce2 ON ce2.chunk_id = c.id\n" +
                        "    JOIN entities e2 ON e2.id = ce2.entity_id\n" +
                        "    WHERE e1.name = ANY(@entity_names) AND e2.name <> e1.name\n" +
                        ")\n" +
                        "SELECT source, relationship, target, document_id, chunk_index, source_text\n" +
                        "FROM (SELECT * FROM typed_relationships UNION ALL SELECT * FROM co_mentions) rels\n" +
                        "ORDER BY priority, document_id DESC NULLS LAST, chunk_index NULLS LAST, source, target\n" +
                        "LIMIT @limit;";
        var triples = relationships.Count == 0
            ? "(none)"
            : string.Join(Environment.NewLine, relationships.Select(r => $"{r.Source} -[{r.Relationship}]-> {r.Target} (doc:{r.DocumentId?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} chunk:{r.ChunkIndex?.ToString(CultureInfo.InvariantCulture) ?? "n/a"})"));

        return string.Join(Environment.NewLine,
        [
            "Purpose: retrieve typed graph triples plus source evidence before chunk ranking; fall back to co-mentioned entities when typed relations are unavailable.",
            string.Empty,
            "SQL:",
            parameterizedSql,
            string.Empty,
            $"Entity filter: {FormatEntityList(entityFilter, 20)}",
            string.Empty,
            "Relationship triples:",
            triples
        ]);
    }

    private static string BuildHybridChunkSearchDetail(IReadOnlyCollection<string> entityFilter, string query, int limit, float[] queryEmbedding)
    {
        const string parameterizedSql = "WITH entity_chunks AS (\n" +
                                        "    SELECT DISTINCT c.id, c.document_id, c.chunk_index, c.content, c.embedding\n" +
                                        "    FROM chunks c\n" +
                                        "    JOIN chunk_entities ce ON ce.chunk_id = c.id\n" +
                                        "    JOIN entities e ON e.id = ce.entity_id\n" +
                                        "    WHERE e.name = ANY(@entity_names)\n" +
                                        "),\n" +
                                        "semantic_search AS (\n" +
                                        "    SELECT id, RANK() OVER (ORDER BY embedding <=> CAST(@query_vector AS vector)) AS vector_rank, embedding <=> CAST(@query_vector AS vector) AS distance\n" +
                                        "    FROM entity_chunks\n" +
                                        "    WHERE embedding IS NOT NULL\n" +
                                        "    ORDER BY embedding <=> CAST(@query_vector AS vector)\n" +
                                        "    LIMIT 20\n" +
                                        "),\n" +
                                        "keyword_search AS (\n" +
                                        "    SELECT id, RANK() OVER (ORDER BY ts_rank_cd(to_tsvector('english', content), plainto_tsquery('english', @query)) DESC) AS keyword_rank\n" +
                                        "    FROM entity_chunks\n" +
                                        "    WHERE to_tsvector('english', content) @@ plainto_tsquery('english', @query)\n" +
                                        "    ORDER BY ts_rank_cd(to_tsvector('english', content), plainto_tsquery('english', @query)) DESC\n" +
                                        "    LIMIT 20\n" +
                                        "),\n" +
                                        "ranked AS (\n" +
                                        "    SELECT COALESCE(s.id, k.id) AS id, s.distance, s.vector_rank, k.keyword_rank,\n" +
                                        "           COALESCE(1.0 / (@rrf_k + s.vector_rank), 0.0) + COALESCE(1.0 / (@rrf_k + k.keyword_rank), 0.0) AS score\n" +
                                        "    FROM semantic_search s\n" +
                                        "    FULL OUTER JOIN keyword_search k ON k.id = s.id\n" +
                                        ")\n" +
                                        "SELECT c.id, c.document_id, c.chunk_index, c.content, r.distance, r.vector_rank, r.keyword_rank, r.score\n" +
                                        "FROM ranked r\n" +
                                        "JOIN chunks c ON c.id = r.id\n" +
                                        "ORDER BY r.score DESC\n" +
                                        "LIMIT @limit;";
        var constrainedLimit = Math.Clamp(limit, 1, MaxVectorResultLimit);
        var vectorLiteral = ToVectorLiteral(queryEmbedding, 1536);
        var entityArray = entityFilter.Count == 0
            ? "ARRAY[]::text[]"
            : $"ARRAY[{string.Join(", ", entityFilter.Select(e => $"'{EscapeSqlLiteral(e)}'"))}]::text[]";
        var safeQuery = EscapeSqlLiteral(query);
        var executableSql = parameterizedSql
            .Replace("@entity_names", entityArray, StringComparison.Ordinal)
            .Replace("CAST(@query_vector AS vector)", "'" + vectorLiteral + "'::vector", StringComparison.Ordinal)
            .Replace("@query", "'" + safeQuery + "'", StringComparison.Ordinal)
            .Replace("@rrf_k", "60", StringComparison.Ordinal)
            .Replace("@limit", constrainedLimit.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        return $"Parameterized SQL:\n{parameterizedSql}\n\nExecutable SQL:\n{executableSql}";
    }

    private static string BuildAnalysisUserPrompt(string question, string? clarification)
    {
        var clarificationText = string.IsNullOrWhiteSpace(clarification) ? string.Empty : $"\nClarification: {clarification}";
        return $"Question: {question}{clarificationText}";
    }

    private static string BuildEntityMatchDetail(IReadOnlyCollection<string> candidates, IReadOnlyCollection<string> expandedCandidates, IReadOnlyCollection<string> matched)
    {
        const string sql = "SELECT name\n" +
                           "FROM entities\n" +
                           "WHERE lower(name) = ANY(@exact)\n" +
                           "   OR name ILIKE ANY(@patterns)\n" +
                           "ORDER BY CASE WHEN lower(name) = ANY(@exact) THEN 0 ELSE 1 END, name\n" +
                           "LIMIT 20;";
        const string semanticSql = "SELECT name, embedding <=> CAST(@query_vector AS vector) AS distance\n" +
                       "FROM entities\n" +
                       "WHERE embedding IS NOT NULL\n" +
                       "ORDER BY embedding <=> CAST(@query_vector AS vector)\n" +
                       "LIMIT 8;";
        var lowered = expandedCandidates.Select(c => c.ToLowerInvariant()).ToList();
        var patterns = expandedCandidates.Select(c => $"%{c}%").ToList();
        var output = matched.Count == 0 ? "(none)" : string.Join(", ", matched);
        var showExpanded = expandedCandidates.Count != candidates.Count
            || expandedCandidates.Any(candidate => !candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase));
        return string.Join(Environment.NewLine,
        [
            "SQL:",
            sql,
            string.Empty,
            "Semantic SQL:",
            semanticSql,
            string.Empty,
            $"Parameters:",
            $"- @exact ({lowered.Count}): {FormatEntityList(lowered, 12)}",
            $"- @patterns ({patterns.Count}): {FormatEntityList(patterns, 12)}",
            showExpanded ? $"- Expanded candidates ({expandedCandidates.Count}): {FormatEntityList(expandedCandidates, 12)}" : "- Expanded candidates: (none)",
            "- Fuzzy fallback: applied in memory against known entity names after exact/pattern matching.",
            string.Empty,
            $"Matched entities: {output}"
        ]);
    }

    private static List<string> ExpandEntityCandidates(IReadOnlyCollection<string> candidates)
    {
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            expanded.Add(candidate.Trim());
            foreach (var variant in GenerateAsciiVariants(candidate))
            {
                if (!string.IsNullOrWhiteSpace(variant))
                {
                    expanded.Add(variant.Trim());
                }
            }
        }

        return expanded.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> GenerateAsciiVariants(string input)
    {
        var variants = new List<string> { string.Empty };
        foreach (var ch in input)
        {
            string[] options = ch switch
            {
                'Å' or 'å' => ["aa"],
                'Æ' or 'æ' => ["ae"],
                'Ø' or 'ø' => ["o"],
                'Ö' or 'ö' => ["o", "oe"],
                _ => [ch.ToString()]
            };

            var next = new List<string>(variants.Count * options.Length);
            foreach (var prefix in variants)
            {
                foreach (var option in options)
                {
                    next.Add(prefix + option);
                }
            }
            variants = next;
        }

        return variants;
    }

    private static string BuildAnswerUserPrompt(string context, string question)
        => $"Context:\n{context}\n\nQuestion:\n{question}";

    private static string BuildPromptDetail(string? systemPrompt, string? userPrompt, string outputLabel, string output)
    {
        var systemText = string.IsNullOrWhiteSpace(systemPrompt) ? "(not available)" : systemPrompt.Trim();
        var userText = string.IsNullOrWhiteSpace(userPrompt) ? "(not available)" : userPrompt.Trim();
        return $"System prompt:\n{systemText}\n\nUser prompt:\n{userText}\n\n{outputLabel}:\n{output}";
    }

    private static string BuildPromptDetailPromptsOnly(string? systemPrompt, string? userPrompt)
    {
        var systemText = string.IsNullOrWhiteSpace(systemPrompt) ? "(not available)" : systemPrompt.Trim();
        var userText = string.IsNullOrWhiteSpace(userPrompt) ? "(not available)" : userPrompt.Trim();
        return $"System prompt:\n{systemText}\n\nUser prompt:\n{userText}";
    }

    private static string BuildPromptDetailSystemOnly(string? systemPrompt)
    {
        var systemText = string.IsNullOrWhiteSpace(systemPrompt) ? "(not available)" : systemPrompt.Trim();
        return $"System prompt:\n{systemText}";
    }

    private static async Task<(List<string> Matched, List<string> ExpandedCandidates)> MatchEntitiesAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<string> candidates,
        float[] queryEmbedding,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return ([], []);
        }

        var expandedCandidates = ExpandEntityCandidates(candidates);
        var lowered = expandedCandidates.Select(c => c.ToLowerInvariant()).ToArray();
        var patterns = expandedCandidates.Select(c => $"%{c}%").ToArray();
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

        await reader.CloseAsync();

        var semanticMatches = await SemanticMatchEntitiesAsync(connection, queryEmbedding, matches, cancellationToken);
        foreach (var match in semanticMatches)
        {
            if (!matches.Contains(match, StringComparer.OrdinalIgnoreCase))
            {
                matches.Add(match);
            }
        }

        var fuzzyMatches = await FuzzyMatchEntitiesAsync(connection, expandedCandidates, matches, cancellationToken);
        foreach (var match in fuzzyMatches)
        {
            if (!matches.Contains(match, StringComparer.OrdinalIgnoreCase))
            {
                matches.Add(match);
            }
        }

        return (matches, expandedCandidates);
    }

    private static async Task<List<string>> SemanticMatchEntitiesAsync(
        NpgsqlConnection connection,
        float[] queryEmbedding,
        IReadOnlyCollection<string> existingMatches,
        CancellationToken cancellationToken)
    {
        if (queryEmbedding.Length == 0)
        {
            return [];
        }

        const string ensureSql = "ALTER TABLE entities ADD COLUMN IF NOT EXISTS embedding vector(1536);";
        await using (var ensureCommand = new NpgsqlCommand(ensureSql, connection))
        {
            await ensureCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var vectorLiteral = ToVectorLiteral(queryEmbedding, 1536);
        const string sql = """
                           SELECT name, embedding <=> CAST(@query_vector AS vector) AS distance
                           FROM entities
                           WHERE embedding IS NOT NULL
                           ORDER BY embedding <=> CAST(@query_vector AS vector)
                           LIMIT 8;
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("query_vector", vectorLiteral);
        var matches = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }

            var name = reader.GetString(0);
            var distance = Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture);
            if (distance <= 0.42 && !existingMatches.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                matches.Add(name);
            }
        }

        return matches;
    }

    private static async Task<List<string>> FuzzyMatchEntitiesAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<string> candidates,
        IReadOnlyCollection<string> existingMatches,
        CancellationToken cancellationToken)
    {
        var normalizedCandidates = candidates
            .Select(candidate => candidate.Trim())
            .Where(candidate => candidate.Length >= 4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedCandidates.Count == 0)
        {
            return [];
        }

        const string sql = "SELECT name FROM entities ORDER BY id DESC LIMIT 1000;";
        await using var command = new NpgsqlCommand(sql, connection);
        var matches = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            var entityName = reader.GetString(0);
            if (existingMatches.Contains(entityName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalizedEntity = NormalizeForFuzzyMatch(entityName);
            foreach (var candidate in normalizedCandidates)
            {
                var normalizedCandidate = NormalizeForFuzzyMatch(candidate);
                if (IsLikelyEntityMatch(normalizedCandidate, normalizedEntity)
                    || TokenizeForFuzzyMatch(entityName).Any(token => IsLikelyEntityMatch(normalizedCandidate, token)))
                {
                    matches.Add(entityName);
                    break;
                }
            }
        }

        return matches
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private static bool IsLikelyEntityMatch(string candidate, string entity)
    {
        if (candidate.Length < 4 || entity.Length < 4)
        {
            return false;
        }

        if (entity.Contains(candidate, StringComparison.OrdinalIgnoreCase) || candidate.Contains(entity, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var suffixLength = Math.Min(4, Math.Min(candidate.Length, entity.Length));
        if (candidate[^suffixLength..].Equals(entity[^suffixLength..], StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var distance = LevenshteinDistance(candidate, entity);
        var maxLength = Math.Max(candidate.Length, entity.Length);
        return maxLength <= 8 && distance <= 2;
    }

    private static string NormalizeForFuzzyMatch(string value)
    {
        var normalized = value.Trim().ToLowerInvariant()
            .Replace("å", "aa", StringComparison.Ordinal)
            .Replace("æ", "ae", StringComparison.Ordinal)
            .Replace("ø", "o", StringComparison.Ordinal)
            .Replace("ö", "o", StringComparison.Ordinal);
        return Regex.Replace(normalized, "[^a-z0-9]", string.Empty);
    }

    private static IEnumerable<string> TokenizeForFuzzyMatch(string value)
        => Regex.Matches(value, "[A-Za-zÅåÆæØøÖö0-9]{4,}")
            .Select(match => NormalizeForFuzzyMatch(match.Value))
            .Where(token => token.Length >= 4);

    private static int LevenshteinDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
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

    private static async Task<GraphNodeDetailsResponse> GetDocumentNodeDetailsAsync(
        NpgsqlConnection connection,
        GraphNodeDetailsRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetLongProperty(request, "id", out var documentId)
            && !TryParsePrefixedId(request.Id, "doc:", out documentId))
        {
            return MissingNodeDetails(request, "Document node does not include a relational document id.");
        }

        const string sql = """
                           SELECT id,
                                  title,
                                  file_name,
                                  document_type,
                                  document_date,
                                  content,
                                  tags::text,
                                  created_at
                           FROM documents
                           WHERE id = @document_id;
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("document_id", documentId);
        IReadOnlyDictionary<string, string?> attributes;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return MissingNodeDetails(request, $"No document row found for id {documentId}.");
            }

            attributes = ReadAttributes(reader, [
                "id",
                "title",
                "file_name",
                "document_type",
                "document_date",
                "content",
                "tags",
                "created_at"
            ]);
        }

        var chunks = await GetDocumentChunksAsync(connection, documentId, cancellationToken);
        return new GraphNodeDetailsResponse(true, null, "Document", attributes, new Dictionary<string, string>
        {
            ["id"] = "documents.id",
            ["title"] = "documents.title",
            ["file_name"] = "documents.file_name",
            ["document_type"] = "documents.document_type",
            ["document_date"] = "documents.document_date",
            ["content"] = "documents.content",
            ["tags"] = "documents.tags",
            ["created_at"] = "documents.created_at"
        }, Chunks: chunks);
    }

    private static async Task<GraphNodeDetailsResponse> GetChunkNodeDetailsAsync(
        NpgsqlConnection connection,
        GraphNodeDetailsRequest request,
        CancellationToken cancellationToken)
    {
        NpgsqlCommand command;
        if (TryGetLongProperty(request, "document_id", out var documentId)
            && TryGetIntProperty(request, "chunk_index", out var chunkIndex))
        {
            const string sql = """
                               SELECT c.id,
                                      c.document_id,
                                      d.title AS document_title,
                                      d.file_name AS document_file_name,
                                      c.chunk_index,
                                      c.content,
                                      string_agg(DISTINCT e.label || ': ' || e.name, ', ' ORDER BY e.label || ': ' || e.name) AS mentioned_entities,
                                      c.created_at
                               FROM chunks c
                               JOIN documents d ON d.id = c.document_id
                               LEFT JOIN chunk_entities ce ON ce.chunk_id = c.id
                               LEFT JOIN entities e ON e.id = ce.entity_id
                               WHERE c.document_id = @document_id AND c.chunk_index = @chunk_index
                               GROUP BY c.id, c.document_id, d.title, d.file_name, c.chunk_index, c.content, c.created_at;
                               """;
            command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("document_id", documentId);
            command.Parameters.AddWithValue("chunk_index", chunkIndex);
        }
        else if (TryParsePrefixedId(request.Id, "chunk:", out var chunkId))
        {
            const string sql = """
                               SELECT c.id,
                                      c.document_id,
                                      d.title AS document_title,
                                      d.file_name AS document_file_name,
                                      c.chunk_index,
                                      c.content,
                                      string_agg(DISTINCT e.label || ': ' || e.name, ', ' ORDER BY e.label || ': ' || e.name) AS mentioned_entities,
                                      c.created_at
                               FROM chunks c
                               JOIN documents d ON d.id = c.document_id
                               LEFT JOIN chunk_entities ce ON ce.chunk_id = c.id
                               LEFT JOIN entities e ON e.id = ce.entity_id
                               WHERE c.id = @chunk_id
                               GROUP BY c.id, c.document_id, d.title, d.file_name, c.chunk_index, c.content, c.created_at;
                               """;
            command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("chunk_id", chunkId);
        }
        else
        {
            return MissingNodeDetails(request, "Chunk node does not include relational document/chunk identifiers.");
        }

        await using (command)
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return MissingNodeDetails(request, "No chunk row found for the selected node.");
            }

            var attributes = ReadAttributes(reader, [
                "id",
                "document_id",
                "document_title",
                "document_file_name",
                "chunk_index",
                "content",
                "mentioned_entities",
                "created_at"
            ]);
            var chunks = new[]
            {
                new GraphNodeChunkDto(
                    ToLong(attributes.GetValueOrDefault("id")),
                    ToLong(attributes.GetValueOrDefault("document_id")),
                    ToInt(attributes.GetValueOrDefault("chunk_index")),
                    attributes.GetValueOrDefault("content") ?? string.Empty,
                    attributes.GetValueOrDefault("document_title"),
                    attributes.GetValueOrDefault("document_file_name"),
                    null,
                    "Selected chunk")
            };

            return new GraphNodeDetailsResponse(true, null, "Chunk", attributes, new Dictionary<string, string>
            {
                ["id"] = "chunks.id",
                ["document_id"] = "chunks.document_id",
                ["document_title"] = "documents.title",
                ["document_file_name"] = "documents.file_name",
                ["chunk_index"] = "chunks.chunk_index",
                ["content"] = "chunks.content",
                ["mentioned_entities"] = "entities.label + entities.name via chunk_entities",
                ["created_at"] = "chunks.created_at"
            }, Chunks: chunks);
        }
    }

    private static async Task<GraphNodeDetailsResponse> GetEntityNodeDetailsAsync(
        NpgsqlConnection connection,
        GraphNodeDetailsRequest request,
        CancellationToken cancellationToken)
    {
        var name = GetProperty(request, "name");
        if (string.IsNullOrWhiteSpace(name) && request.Title.Contains(':', StringComparison.Ordinal))
        {
            name = request.Title[(request.Title.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
        }

        if (string.IsNullOrWhiteSpace(name) && TryParsePrefixedId(request.Id, "entity:", out var entityId))
        {
            const string byIdSql = """
                                   SELECT e.id,
                                          e.label,
                                          e.name,
                                    COUNT(DISTINCT ce.chunk_id)::text AS chunks_mentioning_this_entity,
                                    COUNT(DISTINCT c.document_id)::text AS documents_mentioning_this_entity,
                                    string_agg(DISTINCT 'Chunk ' || c.chunk_index::text, ', ' ORDER BY 'Chunk ' || c.chunk_index::text) AS chunks,
                                          string_agg(DISTINCT d.title, ', ' ORDER BY d.title) AS documents,
                                          e.created_at
                                   FROM entities e
                                   LEFT JOIN chunk_entities ce ON ce.entity_id = e.id
                                   LEFT JOIN chunks c ON c.id = ce.chunk_id
                                   LEFT JOIN documents d ON d.id = c.document_id
                                   WHERE e.id = @entity_id
                                   GROUP BY e.id, e.label, e.name, e.created_at;
                                   """;
            await using var byIdCommand = new NpgsqlCommand(byIdSql, connection);
            byIdCommand.Parameters.AddWithValue("entity_id", entityId);
            return await ReadEntityNodeDetailsAsync(byIdCommand, request, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return MissingNodeDetails(request, "Entity node does not include a name.");
        }

        const string sql = """
                           SELECT e.id,
                                  e.label,
                                  e.name,
                                  COUNT(DISTINCT ce.chunk_id)::text AS chunks_mentioning_this_entity,
                                  COUNT(DISTINCT c.document_id)::text AS documents_mentioning_this_entity,
                                  string_agg(DISTINCT 'Chunk ' || c.chunk_index::text, ', ' ORDER BY 'Chunk ' || c.chunk_index::text) AS chunks,
                                  string_agg(DISTINCT d.title, ', ' ORDER BY d.title) AS documents,
                                  e.created_at
                           FROM entities e
                           LEFT JOIN chunk_entities ce ON ce.entity_id = e.id
                           LEFT JOIN chunks c ON c.id = ce.chunk_id
                           LEFT JOIN documents d ON d.id = c.document_id
                           WHERE e.name = @name
                           GROUP BY e.id, e.label, e.name, e.created_at;
                           """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("name", name);
        return await ReadEntityNodeDetailsAsync(command, request, cancellationToken);
    }

    private static async Task<GraphNodeDetailsResponse> ReadEntityNodeDetailsAsync(
        NpgsqlCommand command,
        GraphNodeDetailsRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string?> attributes;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return MissingNodeDetails(request, "No entity row found for the selected node.");
            }

            attributes = ReadAttributes(reader, [
                "id",
                "label",
                "name",
                "chunks_mentioning_this_entity",
                "documents_mentioning_this_entity",
                "chunks",
                "documents",
                "created_at"
            ]);
        }

        var entityId = attributes.TryGetValue("id", out var idValue) && long.TryParse(idValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId)
            ? parsedId
            : 0;
        var sources = new Dictionary<string, string>
        {
            ["id"] = "kg_data.entities.id",
            ["label"] = "kg_data.entities.label",
            ["name"] = "kg_data.entities.name",
            ["chunks_mentioning_this_entity"] = "COUNT(DISTINCT chunk_entities.chunk_id) WHERE chunk_entities.entity_id = entities.id",
            ["documents_mentioning_this_entity"] = "COUNT(DISTINCT chunks.document_id) WHERE chunk_entities.entity_id = entities.id",
            ["chunks"] = "string_agg(DISTINCT chunks.chunk_index) WHERE chunk_entities.entity_id = entities.id",
            ["documents"] = "string_agg(DISTINCT documents.title) WHERE chunk_entities.entity_id = entities.id",
            ["created_at"] = "kg_data.entities.created_at"
        };
        var sourceSqls = new Dictionary<string, string>
        {
            ["chunks_mentioning_this_entity"] = $"SELECT COUNT(DISTINCT ce.chunk_id)\nFROM kg_data.entities e\nLEFT JOIN kg_data.chunk_entities ce ON ce.entity_id = e.id\nWHERE e.id = {entityId};",
            ["documents_mentioning_this_entity"] = $"SELECT COUNT(DISTINCT c.document_id)\nFROM kg_data.entities e\nLEFT JOIN kg_data.chunk_entities ce ON ce.entity_id = e.id\nLEFT JOIN kg_data.chunks c ON c.id = ce.chunk_id\nWHERE e.id = {entityId};",
            ["chunks"] = $"SELECT string_agg(DISTINCT 'Chunk ' || c.chunk_index::text, ', ' ORDER BY 'Chunk ' || c.chunk_index::text)\nFROM kg_data.entities e\nLEFT JOIN kg_data.chunk_entities ce ON ce.entity_id = e.id\nLEFT JOIN kg_data.chunks c ON c.id = ce.chunk_id\nWHERE e.id = {entityId};",
            ["documents"] = $"SELECT string_agg(DISTINCT d.title, ', ' ORDER BY d.title)\nFROM kg_data.entities e\nLEFT JOIN kg_data.chunk_entities ce ON ce.entity_id = e.id\nLEFT JOIN kg_data.chunks c ON c.id = ce.chunk_id\nLEFT JOIN kg_data.documents d ON d.id = c.document_id\nWHERE e.id = {entityId};"
        };

        var chunks = command.Connection is null || entityId == 0
            ? []
            : await GetEntityChunksAsync(command.Connection, entityId, cancellationToken);

        return new GraphNodeDetailsResponse(true, null, "Entity", attributes, sources, sourceSqls, chunks);
    }

    private static async Task<IReadOnlyList<GraphNodeChunkDto>> GetDocumentChunksAsync(NpgsqlConnection connection, long documentId, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT c.id,
                                  c.document_id,
                                  c.chunk_index,
                                  c.content,
                                  d.title AS document_title,
                                  d.file_name AS document_file_name,
                                  d.document_date::text AS document_date
                           FROM chunks c
                           JOIN documents d ON d.id = c.document_id
                           WHERE c.document_id = @document_id
                           ORDER BY c.chunk_index
                           LIMIT 50;
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("document_id", documentId);
        return await ReadNodeChunksAsync(command, "In selected document", cancellationToken);
    }

    private static async Task<IReadOnlyList<GraphNodeChunkDto>> GetEntityChunksAsync(NpgsqlConnection connection, long entityId, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT c.id,
                                  c.document_id,
                                  c.chunk_index,
                                  c.content,
                                  d.title AS document_title,
                                  d.file_name AS document_file_name,
                                  d.document_date::text AS document_date
                           FROM chunks c
                           JOIN chunk_entities ce ON ce.chunk_id = c.id
                           JOIN documents d ON d.id = c.document_id
                           WHERE ce.entity_id = @entity_id
                           ORDER BY d.document_date DESC NULLS LAST, d.title, c.chunk_index
                           LIMIT 50;
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("entity_id", entityId);
        return await ReadNodeChunksAsync(command, "Mentions selected entity", cancellationToken);
    }

    private static async Task<IReadOnlyList<GraphNodeChunkDto>> ReadNodeChunksAsync(NpgsqlCommand command, string linkReason, CancellationToken cancellationToken)
    {
        var chunks = new List<GraphNodeChunkDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            chunks.Add(new GraphNodeChunkDto(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                linkReason));
        }

        return chunks;
    }

    private static long ToLong(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static int ToInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static GraphNodeDetailsResponse MissingNodeDetails(GraphNodeDetailsRequest request, string message)
        => new(false, message, string.IsNullOrWhiteSpace(request.Label) ? "Node" : request.Label, new Dictionary<string, string?>(), new Dictionary<string, string>());

    private static IReadOnlyDictionary<string, string?> ReadAttributes(NpgsqlDataReader reader, IReadOnlyList<string> names)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount && i < names.Count; i++)
        {
            attributes[names[i]] = GetFieldValueAsString(reader, i);
        }

        return attributes;
    }

    private static bool TryGetLongProperty(GraphNodeDetailsRequest request, string key, out long value)
        => long.TryParse(GetProperty(request, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryGetIntProperty(GraphNodeDetailsRequest request, string key, out int value)
        => int.TryParse(GetProperty(request, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string? GetProperty(GraphNodeDetailsRequest request, string key)
    {
        if (request.Properties is null)
        {
            return null;
        }

        return request.Properties.TryGetValue(key, out var value) ? value : null;
    }

    private static bool TryParsePrefixedId(string id, string prefix, out long value)
    {
        value = 0;
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return long.TryParse(id[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static async Task<List<HybridGraphRelationshipDto>> QueryGraphRelationshipsAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<string> entities,
        int limit,
        CancellationToken cancellationToken)
    {
        if (entities.Count == 0)
        {
            return [];
        }

        var constrainedLimit = Math.Clamp(limit, 1, MaxVectorResultLimit);
        const string ensureSql = """
                               CREATE TABLE IF NOT EXISTS relationships (
                                   id BIGSERIAL PRIMARY KEY,
                                   source_entity_id BIGINT NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
                                   target_entity_id BIGINT NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
                                   relationship_type TEXT NOT NULL,
                                   chunk_id BIGINT NULL REFERENCES chunks(id) ON DELETE SET NULL,
                                   confidence DOUBLE PRECISION NULL,
                                   evidence TEXT NULL,
                                   created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                                   UNIQUE(source_entity_id, target_entity_id, relationship_type, chunk_id)
                               );
                               """;
        await using (var ensureCommand = new NpgsqlCommand(ensureSql, connection))
        {
            await ensureCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string sql = """
                           WITH typed_relationships AS (
                               SELECT s.name AS source,
                                      r.relationship_type AS relationship,
                                      t.name AS target,
                                      c.document_id,
                                      c.chunk_index,
                                      COALESCE(r.evidence, c.content) AS source_text,
                                      0 AS priority
                               FROM relationships r
                               JOIN entities s ON s.id = r.source_entity_id
                               JOIN entities t ON t.id = r.target_entity_id
                               LEFT JOIN chunks c ON c.id = r.chunk_id
                               WHERE s.name = ANY(@entity_names)
                                  OR t.name = ANY(@entity_names)
                           ),
                           co_mentions AS (
                               SELECT e1.name AS source,
                                      'CO_MENTIONED_WITH' AS relationship,
                                      e2.name AS target,
                                      c.document_id,
                                      c.chunk_index,
                                      c.content AS source_text,
                                      1 AS priority
                               FROM chunks c
                               JOIN chunk_entities ce1 ON ce1.chunk_id = c.id
                               JOIN entities e1 ON e1.id = ce1.entity_id
                               JOIN chunk_entities ce2 ON ce2.chunk_id = c.id
                               JOIN entities e2 ON e2.id = ce2.entity_id
                               WHERE e1.name = ANY(@entity_names)
                                 AND e2.name <> e1.name
                           )
                           SELECT source, relationship, target, document_id, chunk_index, source_text
                           FROM (
                               SELECT * FROM typed_relationships
                               UNION ALL
                               SELECT * FROM co_mentions
                           ) rels
                           ORDER BY priority, document_id DESC NULLS LAST, chunk_index NULLS LAST, source, target
                           LIMIT @limit;
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("entity_names", entities.ToArray());
        command.Parameters.AddWithValue("limit", constrainedLimit);

        var relationships = new List<HybridGraphRelationshipDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            relationships.Add(new HybridGraphRelationshipDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        await reader.CloseAsync();
        return relationships;
    }

    private static async Task<List<HybridChunkResultDto>> QueryChunksByEntitiesHybridAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<string> entities,
        string query,
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
                           WITH entity_chunks AS (
                               SELECT DISTINCT c.id, c.document_id, c.chunk_index, c.content, c.embedding
                               FROM chunks c
                               JOIN chunk_entities ce ON ce.chunk_id = c.id
                               JOIN entities e ON e.id = ce.entity_id
                               WHERE e.name = ANY(@entity_names)
                           ),
                           semantic_search AS (
                               SELECT id,
                                      RANK() OVER (ORDER BY embedding <=> CAST(@query_vector AS vector)) AS vector_rank,
                                      embedding <=> CAST(@query_vector AS vector) AS distance
                               FROM entity_chunks
                               WHERE embedding IS NOT NULL
                               ORDER BY embedding <=> CAST(@query_vector AS vector)
                               LIMIT 20
                           ),
                           keyword_search AS (
                               SELECT id,
                                      RANK() OVER (ORDER BY ts_rank_cd(to_tsvector('english', content), plainto_tsquery('english', @query)) DESC) AS keyword_rank
                               FROM entity_chunks
                               WHERE to_tsvector('english', content) @@ plainto_tsquery('english', @query)
                               ORDER BY ts_rank_cd(to_tsvector('english', content), plainto_tsquery('english', @query)) DESC
                               LIMIT 20
                           ),
                           ranked AS (
                               SELECT COALESCE(s.id, k.id) AS id,
                                      s.distance,
                                      s.vector_rank,
                                      k.keyword_rank,
                                      COALESCE(1.0 / (@rrf_k + s.vector_rank), 0.0) + COALESCE(1.0 / (@rrf_k + k.keyword_rank), 0.0) AS score
                               FROM semantic_search s
                               FULL OUTER JOIN keyword_search k ON k.id = s.id
                           )
                           SELECT c.id,
                                  c.document_id,
                                  c.chunk_index,
                                  c.content,
                                  r.distance,
                                  r.vector_rank,
                                  r.keyword_rank,
                                  r.score
                           FROM ranked r
                           JOIN chunks c ON c.id = r.id
                           ORDER BY r.score DESC
                           LIMIT @limit;
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("entity_names", entities.ToArray());
        command.Parameters.AddWithValue("query_vector", vectorLiteral);
        command.Parameters.AddWithValue("query", query);
        command.Parameters.AddWithValue("rrf_k", 60);
        command.Parameters.AddWithValue("limit", constrainedLimit);

        var chunks = new List<HybridChunkResultDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var documentId = reader.GetInt64(1);
            var chunkIndex = reader.GetInt32(2);
            var content = reader.GetString(3);
            double? distance = reader.IsDBNull(4) ? null : Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture);
            int? vectorRank = reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture);
            int? keywordRank = reader.IsDBNull(6) ? null : Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture);
            double? score = reader.IsDBNull(7) ? null : Convert.ToDouble(reader.GetValue(7), CultureInfo.InvariantCulture);
            chunks.Add(new HybridChunkResultDto(id, documentId, chunkIndex, content, distance, vectorRank, keywordRank, score));
        }

        await reader.CloseAsync();
        return chunks;
    }

    private static string BuildPromptContext(
        IReadOnlyCollection<string> graphEntities,
        IReadOnlyCollection<HybridGraphRelationshipDto> graphRelationships,
        IReadOnlyCollection<HybridChunkResultDto> chunks)
    {
        var graphFactSection = graphEntities.Count == 0
            ? "No graph entities found."
            : $"Graph entities ({graphEntities.Count}): {string.Join(", ", graphEntities)}";

        var relationshipSection = graphRelationships.Count == 0
            ? "No graph relationship triples found."
            : string.Join(Environment.NewLine, graphRelationships.Select(r => $"- {r.Source} -[{r.Relationship}]-> {r.Target} (doc:{r.DocumentId?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} chunk:{r.ChunkIndex?.ToString(CultureInfo.InvariantCulture) ?? "n/a"})"));

        var chunkSection = chunks.Count == 0
            ? "No hybrid-ranked chunks found (ensure embeddings and chunk text are populated)."
            : string.Join(Environment.NewLine, chunks.Select(c => $"- [doc:{c.DocumentId} chunk:{c.ChunkIndex} score:{c.Score?.ToString("0.####", CultureInfo.InvariantCulture) ?? "n/a"}] {c.Content}"));

        return $"{graphFactSection}{Environment.NewLine}{Environment.NewLine}Graph relationship triples:{Environment.NewLine}{relationshipSection}{Environment.NewLine}{Environment.NewLine}Relevant chunks:{Environment.NewLine}{chunkSection}";
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
                nodes[nodeId] = new GraphNodeDto(
                    nodeId,
                    "Document",
                    string.IsNullOrWhiteSpace(title) ? "Document" : $"Document: {title}",
                    new Dictionary<string, string?> { ["id"] = id.ToString(CultureInfo.InvariantCulture) });
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
                nodes[nodeId] = new GraphNodeDto(
                    nodeId,
                    "Chunk",
                    $"Chunk {chunkIndex}",
                    new Dictionary<string, string?>
                    {
                        ["id"] = id.ToString(CultureInfo.InvariantCulture),
                        ["document_id"] = documentId.ToString(CultureInfo.InvariantCulture),
                        ["chunk_index"] = chunkIndex.ToString(CultureInfo.InvariantCulture)
                    });
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
                nodes[nodeId] = new GraphNodeDto(
                    nodeId,
                    label,
                    string.IsNullOrWhiteSpace(name) ? label : $"{label}: {name}",
                    new Dictionary<string, string?>
                    {
                        ["id"] = id.ToString(CultureInfo.InvariantCulture),
                        ["name"] = name
                    });
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
                IReadOnlyDictionary<string, string?>? graphProperties = null;
                if (element.TryGetProperty("properties", out var properties)
                    && properties.ValueKind == JsonValueKind.Object)
                {
                    graphProperties = ReadJsonProperties(properties);
                    if (properties.TryGetProperty("name", out var nameValue))
                    {
                        title = $"{label}: {nameValue.GetString()}";
                    }
                    else if (properties.TryGetProperty("title", out var titleValue))
                    {
                        title = $"{label}: {titleValue.GetString()}";
                    }
                    else if (properties.TryGetProperty("chunk_index", out var chunkIndexValue))
                    {
                        title = $"{label} {chunkIndexValue}";
                    }
                }

                if (!string.IsNullOrWhiteSpace(id))
                {
                    nodes[id] = new GraphNodeDto(id, label, title, graphProperties);
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
        IReadOnlyDictionary<string, string?>? graphProperties = null;
        if (root.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            graphProperties = ReadJsonProperties(properties);
            if (properties.TryGetProperty("name", out var nameValue))
            {
                title = $"{label}: {nameValue.GetString()}";
            }
            else if (properties.TryGetProperty("title", out var titleValue))
            {
                title = $"{label}: {titleValue.GetString()}";
            }
            else if (properties.TryGetProperty("chunk_index", out var chunkIndexValue))
            {
                title = $"{label} {chunkIndexValue}";
            }
        }

        nodes[id] = new GraphNodeDto(id, label, title, graphProperties);
    }

    private static IReadOnlyDictionary<string, string?> ReadJsonProperties(JsonElement properties)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.ToString(),
                JsonValueKind.Null => null,
                _ => property.Value.GetRawText()
            };
        }

        return result;
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
