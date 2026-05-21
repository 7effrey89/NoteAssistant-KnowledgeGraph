using System.Diagnostics;
using Microsoft.Extensions.Options;
using NoteAssistant.KnowledgeGraph.Backend.Models;
using NoteAssistant.KnowledgeGraph.Backend.Services;

var builder = WebApplication.CreateBuilder(args);
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        var origins = allowedOrigins is { Length: > 0 }
            ? allowedOrigins
            : ["http://localhost:5272", "https://localhost:7260"];

        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
    });
});

builder.Services.AddSingleton<IngestionStore>();
builder.Services.Configure<FoundryOptions>(builder.Configuration.GetSection("Copilot"));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));
builder.Services.Configure<AnalysisCacheOptions>(builder.Configuration.GetSection("AnalysisCache"));
builder.Services.AddSingleton<IFoundryInferenceClient, FoundryInferenceClient>();
builder.Services.AddSingleton<IAgeDatabaseConnectionFactory, AgeDatabaseConnectionFactory>();
builder.Services.AddSingleton<IAnalysisCache, AnalysisCache>();
builder.Services.AddSingleton<IMarkdownGraphIngestionService, MarkdownGraphIngestionService>();
builder.Services.AddSingleton<QueryAssistantService>();
builder.Services.AddSingleton<AgeGraphRepository>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Frontend");
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

static string NormalizeSchemaName(string? value)
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

static string? NormalizeOptional(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    var trimmed = value.Trim();
    return trimmed.Length == 0 ? null : trimmed;
}

static DateOnly? ParseDateOnly(string? value)
    => DateOnly.TryParse(value, out var date) ? date : null;

static IReadOnlyList<string> ParseTags(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return Array.Empty<string>();
    }

    return value
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(tag => tag.Trim())
        .Where(tag => !string.IsNullOrWhiteSpace(tag))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static DocumentMetadata ParseMetadata(IFormCollection form)
{
    var documentType = NormalizeOptional(form["documentType"].ToString());
    var documentDate = ParseDateOnly(form["documentDate"].ToString());
    var tags = ParseTags(form["tags"].ToString());

    return new DocumentMetadata(documentType, documentDate, tags);
}

app.MapPost("/api/documents/upload", async (HttpRequest request, IMarkdownGraphIngestionService ingestionService, IAnalysisCache cache, IngestionStore store, CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Upload must use multipart/form-data." });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files["file"] ?? form.Files.FirstOrDefault();

    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "A markdown file is required." });
    }

    if (!file.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Only .md files are supported." });
    }

    string markdown;
    using (var reader = new StreamReader(file.OpenReadStream()))
    {
        markdown = await reader.ReadToEndAsync(cancellationToken);
    }

    var metadata = ParseMetadata(form);
    var contentHash = cache.ComputeHash(markdown);
    var cached = await cache.TryGetAsync(contentHash, cancellationToken);
    GraphIngestionPlan plan;
    if (cached is not null)
    {
        var title = Path.GetFileNameWithoutExtension(file.FileName);
        var refreshed = ingestionService.RefreshSql(cached with { Metadata = metadata });
        plan = refreshed with
        {
            Cached = true,
            Title = title,
            Status = cached.Status with
            {
                FileName = file.FileName,
                State = "Cached",
                UpdatedAt = DateTimeOffset.UtcNow,
                Message = "Loaded from local cache for document-to-entity breakdown (no Foundry call). Click 'Ingest' to push into PostgreSQL/AGE."
            }
        };
        store.Upsert(plan.Status);
        store.SavePlan(plan);
        return Results.Ok(plan);
    }

    plan = await ingestionService.CreateGraphPlanAsync(file.FileName, markdown, metadata, cancellationToken);
    var analyzed = plan.Status with
    {
        State = "Analyzed",
        Message = "Decomposition ready. Click 'Ingest' to push into PostgreSQL/AGE."
    };
    plan = plan with { ContentHash = contentHash, Status = analyzed };

    store.Upsert(analyzed);
    store.SavePlan(plan);
    await cache.SaveAsync(plan, cancellationToken);
    return Results.Ok(plan);
})
.DisableAntiforgery();

app.MapPost("/api/documents/{documentId:int}/ingest", async (int documentId, IngestionStore store, AgeGraphRepository repository, CancellationToken cancellationToken) =>
{
    var plan = store.GetPlan(documentId);
    if (plan is null)
    {
        return Results.NotFound(new { error = $"No analyzed plan found for document {documentId}. Upload it first." });
    }

    if (!repository.IsConfigured)
    {
        var notConfigured = plan.Status with
        {
            State = "Ready",
            Message = "Deployment scripts and SQL generated. Configure ConnectionStrings:AgeDatabase to execute directly."
        };
        store.Upsert(notConfigured);
        var updatedPlan = plan with { Status = notConfigured };
        store.SavePlan(updatedPlan);
        return Results.Ok(updatedPlan);
    }

    var relationalStatements = plan.SqlStatements
        .Where(statement => !statement.Contains("cypher(", StringComparison.OrdinalIgnoreCase))
        .ToList();

    var execution = await repository.TryExecuteStatementsWithLogAsync(relationalStatements, cancellationToken);
    store.SaveExecutionLog(documentId, execution.Log);
    var updated = plan.Status with
    {
        State = execution.Success ? "Completed" : "Failed",
        Message = execution.Success
            ? "Relational data ingested. Run step 4 to build the AGE graph."
            : execution.ErrorMessage ?? "Ingestion failed."
    };
    store.Upsert(updated);
    var ingestedPlan = plan with { Status = updated };
    store.SavePlan(ingestedPlan);
    return execution.Success ? Results.Ok(ingestedPlan) : Results.Problem(updated.Message, statusCode: StatusCodes.Status500InternalServerError);
});

app.MapGet("/api/documents/{documentId:int}/ingest/preview", (int documentId, IngestionStore store) =>
{
    var plan = store.GetPlan(documentId);
    if (plan is null)
    {
        return Results.NotFound(new { error = $"No analyzed plan found for document {documentId}. Upload it first." });
    }

    var relational = plan.SqlStatements
        .Where(statement => !statement.Contains("cypher(", StringComparison.OrdinalIgnoreCase))
        .ToList();
    var graph = plan.SqlStatements
        .Where(statement => statement.Contains("cypher(", StringComparison.OrdinalIgnoreCase))
        .ToList();

    return Results.Ok(new { relationalStatements = relational, graphStatements = graph });
});

app.MapGet("/api/documents/{documentId:int}/ingest/log", (int documentId, IngestionStore store) =>
{
    var log = store.GetExecutionLog(documentId);
    return log is null
        ? Results.NotFound(new { error = $"No execution log found for document {documentId}." })
        : Results.Ok(log);
});

app.MapPost("/api/documents/{documentId:int}/graph", async (int documentId, IngestionStore store, AgeGraphRepository repository, IOptions<DatabaseOptions> options, CancellationToken cancellationToken) =>
{
    var plan = store.GetPlan(documentId);
    if (plan is null)
    {
        return Results.NotFound(new { error = $"No analyzed plan found for document {documentId}. Upload it first." });
    }

    if (!repository.IsConfigured)
    {
        return Results.Problem("Database settings are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var graphName = plan.GraphName.Replace("'", "''", StringComparison.Ordinal);
    var graphStatements = new List<string>
    {
        "SET search_path = ag_catalog, \"$user\", public;",
        $"SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM ag_catalog.ag_graph WHERE name = '{graphName}') THEN create_graph('{graphName}') END;"
    };

    graphStatements.AddRange(plan.SqlStatements.Where(statement => statement.Contains("cypher(", StringComparison.OrdinalIgnoreCase)));

    var execution = await repository.TryExecuteStatementsAsync(graphStatements, cancellationToken);
    var updated = plan.Status with
    {
        State = execution.Success ? "Completed" : "Failed",
        Message = execution.Success ? "AGE graph built from current document." : execution.ErrorMessage ?? "Graph build failed."
    };
    store.Upsert(updated);
    var updatedPlan = plan with { Status = updated };
    store.SavePlan(updatedPlan);
    return execution.Success ? Results.Ok(updatedPlan) : Results.Problem(updated.Message, statusCode: StatusCodes.Status500InternalServerError);
});

app.MapGet("/api/documents/{documentId:int}/status", (int documentId, IngestionStore store) =>
{
    var status = store.Get(documentId);
    return status is null
        ? Results.NotFound(new { error = $"No document found for id {documentId}." })
        : Results.Ok(status);
});

app.MapPost("/api/query", async (GraphQueryRequest request, AgeGraphRepository repository, CancellationToken cancellationToken) =>
{
    var response = await repository.ExecuteSelectQueryAsync(request, cancellationToken);
    return response.Success ? Results.Ok(response) : Results.BadRequest(response);
});

app.MapPost("/api/query/assist", (QueryAssistantRequest request, QueryAssistantService assistant) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt))
    {
        return Results.BadRequest(new { error = "Prompt is required." });
    }

    return Results.Ok(assistant.Suggest(request.Prompt));
});

app.MapPost("/api/retrieval/hybrid", async (HybridRetrievalRequest request, AgeGraphRepository repository, CancellationToken cancellationToken) =>
{
    var response = await repository.ExecuteHybridRetrievalAsync(request, cancellationToken);
    return response.Success ? Results.Ok(response) : Results.BadRequest(response);
});

app.MapGet("/api/health/foundry", async (IFoundryInferenceClient foundry, CancellationToken cancellationToken) =>
{
    if (!foundry.IsConfigured)
    {
        return Results.Problem("Foundry inference is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        _ = await foundry.CreateEmbeddingAsync("health-check", cancellationToken);
        return Results.Ok(new { status = "ok" });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/health/db", async (IAgeDatabaseConnectionFactory connectionFactory, CancellationToken cancellationToken) =>
{
    if (!connectionFactory.IsConfigured)
    {
        return Results.Problem("Database settings are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(cancellationToken);
        return Results.Ok(new { status = "ok" });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/graph/init", async (IAgeDatabaseConnectionFactory connectionFactory, IOptions<DatabaseOptions> options, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    if (!connectionFactory.IsConfigured)
    {
        return Results.Problem("Database settings are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var stopwatch = Stopwatch.StartNew();
    try
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeoutCts.Token;

        logger.LogInformation("AGE graph init starting for {GraphName}.", options.Value.GraphName);

        await using var connection = await connectionFactory.OpenAsync(token);
        await using var searchPathCommand = new Npgsql.NpgsqlCommand("SET search_path = ag_catalog, \"$user\", public;", connection)
        {
            CommandTimeout = 5
        };
        await searchPathCommand.ExecuteNonQueryAsync(token);

        await using var graphCommand = new Npgsql.NpgsqlCommand("SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM ag_catalog.ag_graph WHERE name = @graph_name) THEN create_graph(@graph_name) END;", connection)
        {
            CommandTimeout = 5
        };
        graphCommand.Parameters.AddWithValue("graph_name", options.Value.GraphName);
        await graphCommand.ExecuteScalarAsync(token);

        logger.LogInformation("AGE graph init completed for {GraphName} in {ElapsedMs}ms.", options.Value.GraphName, stopwatch.ElapsedMilliseconds);
        return Results.Ok(new { status = "ok", graphName = options.Value.GraphName });
    }
    catch (OperationCanceledException)
    {
        logger.LogWarning("AGE graph init timed out after {ElapsedMs}ms.", stopwatch.ElapsedMilliseconds);
        return Results.Problem("AGE graph initialization timed out.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Npgsql.PostgresException ex)
    {
        logger.LogWarning(ex, "AGE graph init failed after {ElapsedMs}ms.", stopwatch.ElapsedMilliseconds);
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "AGE graph init failed after {ElapsedMs}ms.", stopwatch.ElapsedMilliseconds);
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/graph/info", (IOptions<DatabaseOptions> options) =>
{
    return Results.Ok(new { graphName = options.Value.GraphName });
});

app.MapGet("/api/graph/check", async (IAgeDatabaseConnectionFactory connectionFactory, IOptions<DatabaseOptions> options, CancellationToken cancellationToken) =>
{
    if (!connectionFactory.IsConfigured)
    {
        return Results.Problem("Database settings are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        var schema = NormalizeSchemaName(options.Value.SchemaName);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT " +
            $"(SELECT COUNT(*) FROM \"{schema}\".documents) AS documents, " +
            $"(SELECT COUNT(*) FROM \"{schema}\".chunks) AS chunks, " +
            $"(SELECT COUNT(*) FROM \"{schema}\".entities) AS entities, " +
            $"(SELECT COUNT(*) FROM \"{schema}\".chunk_entities) AS mentions, " +
            "EXISTS (SELECT 1 FROM ag_catalog.ag_graph WHERE name = @graph_name) AS graph_exists;",
            connection);
        command.Parameters.AddWithValue("graph_name", options.Value.GraphName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return Results.Problem("Graph check returned no rows.", statusCode: StatusCodes.Status500InternalServerError);
        }

        var documents = reader.GetInt64(reader.GetOrdinal("documents"));
        var chunks = reader.GetInt64(reader.GetOrdinal("chunks"));
        var entities = reader.GetInt64(reader.GetOrdinal("entities"));
        var mentions = reader.GetInt64(reader.GetOrdinal("mentions"));
        var graphExists = reader.GetBoolean(reader.GetOrdinal("graph_exists"));

        return Results.Ok(new
        {
            graphName = options.Value.GraphName,
            graphExists,
            documents,
            chunks,
            entities,
            mentions
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/db/reset", async (IAgeDatabaseConnectionFactory connectionFactory, IOptions<DatabaseOptions> options, IHostEnvironment environment, IAnalysisCache cache, CancellationToken cancellationToken) =>
{
    if (!environment.IsDevelopment())
    {
        return Results.Problem("Reset is only available in Development.", statusCode: StatusCodes.Status403Forbidden);
    }

    if (!connectionFactory.IsConfigured)
    {
        return Results.Problem("Database settings are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var searchPath = new Npgsql.NpgsqlCommand("SET search_path = ag_catalog, \"$user\", public;", connection);
        await searchPath.ExecuteNonQueryAsync(cancellationToken);

        var graphName = options.Value.GraphName.Replace("'", "''", StringComparison.Ordinal);
        await using var dropGraph = new Npgsql.NpgsqlCommand(
            $"DO $$ BEGIN PERFORM ag_catalog.drop_graph('{graphName}', true); EXCEPTION WHEN OTHERS THEN NULL; END $$;",
            connection);
        await dropGraph.ExecuteNonQueryAsync(cancellationToken);

        var graphSchema = NormalizeSchemaName(options.Value.GraphName);
        await using var dropGraphSchema = new Npgsql.NpgsqlCommand(
            $"DO $$ BEGIN EXECUTE format('DROP SCHEMA %I CASCADE', '{graphSchema}'); EXCEPTION WHEN invalid_schema_name THEN NULL; END $$;",
            connection);
        await dropGraphSchema.ExecuteNonQueryAsync(cancellationToken);

        var schema = NormalizeSchemaName(options.Value.SchemaName);
        await using var truncateSchemaTables = new Npgsql.NpgsqlCommand(
            $"DO $$ BEGIN EXECUTE format('TRUNCATE TABLE %I.documents, %I.chunks, %I.entities, %I.chunk_entities CASCADE', '{schema}', '{schema}', '{schema}', '{schema}'); EXCEPTION WHEN undefined_table OR invalid_schema_name THEN NULL; END $$;",
            connection);
        await truncateSchemaTables.ExecuteNonQueryAsync(cancellationToken);

        await using var truncatePublicTables = new Npgsql.NpgsqlCommand(
            "DO $$ BEGIN TRUNCATE TABLE public.chunk_entities, public.chunks, public.entities, public.documents CASCADE; EXCEPTION WHEN undefined_table THEN NULL; END $$;",
            connection);
        await truncatePublicTables.ExecuteNonQueryAsync(cancellationToken);

        await using var dropSchema = new Npgsql.NpgsqlCommand(
            $"DO $$ BEGIN EXECUTE format('DROP SCHEMA %I CASCADE', '{schema}'); EXCEPTION WHEN invalid_schema_name THEN NULL; END $$;",
            connection);
        await dropSchema.ExecuteNonQueryAsync(cancellationToken);

        await using var dropPublicTables = new Npgsql.NpgsqlCommand(
            "DO $$ BEGIN DROP TABLE public.chunk_entities, public.chunks, public.entities, public.documents CASCADE; EXCEPTION WHEN undefined_table THEN NULL; END $$;",
            connection);
        await dropPublicTables.ExecuteNonQueryAsync(cancellationToken);

        await using var dropAge = new Npgsql.NpgsqlCommand(
            "DO $$ BEGIN DROP EXTENSION age CASCADE; EXCEPTION WHEN undefined_object THEN NULL; END $$;",
            connection);
        await dropAge.ExecuteNonQueryAsync(cancellationToken);

        var cacheDir = cache.CacheDirectory;
        var deleted = 0;
        if (Directory.Exists(cacheDir))
        {
            foreach (var file in Directory.EnumerateFiles(cacheDir, "*.json"))
            {
                File.Delete(file);
                deleted++;
            }
        }

        return Results.Ok(new
        {
            status = "reset",
            graphName = options.Value.GraphName,
            cacheCleared = deleted,
            ageExtensionDropped = true
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/db/init", async (IAgeDatabaseConnectionFactory connectionFactory, IHostEnvironment environment, CancellationToken cancellationToken) =>
{
    if (!environment.IsDevelopment())
    {
        return Results.Problem("Init is only available in Development.", statusCode: StatusCodes.Status403Forbidden);
    }

    if (!connectionFactory.IsConfigured)
    {
        return Results.Problem("Database settings are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var scriptPath = Path.Combine(environment.ContentRootPath, "Deployment", "init", "01-age-init.sql");
    if (!File.Exists(scriptPath))
    {
        return Results.Problem($"Init script not found at {scriptPath}.", statusCode: StatusCodes.Status404NotFound);
    }

    try
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var script = await File.ReadAllTextAsync(scriptPath, cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(script, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return Results.Ok(new { status = "initialized", script });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/health/age", async (IAgeDatabaseConnectionFactory connectionFactory, IOptions<DatabaseOptions> options, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    var stopwatch = Stopwatch.StartNew();
    if (!connectionFactory.IsConfigured)
    {
        return Results.Problem("Database settings are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeoutCts.Token;

        logger.LogInformation("AGE health check starting for graph {GraphName}.", options.Value.GraphName);

        await using var connection = await connectionFactory.OpenAsync(token);
        const string sql = """
                           SELECT EXISTS (
                               SELECT 1
                               FROM pg_proc p
                               JOIN pg_namespace n ON n.oid = p.pronamespace
                               WHERE n.nspname = 'ag_catalog'
                                 AND p.proname = 'cypher'
                           );
                           """;
        await using var command = new Npgsql.NpgsqlCommand(sql, connection)
        {
            CommandTimeout = 5
        };
        var cypherExists = (bool?)await command.ExecuteScalarAsync(token) ?? false;
        if (!cypherExists)
        {
            logger.LogWarning("AGE cypher() function missing after {ElapsedMs}ms.", stopwatch.ElapsedMilliseconds);
            return Results.Problem("AGE cypher() function is not available. Ensure the age extension is installed and the init script has run.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        await using var graphCommand = new Npgsql.NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM ag_catalog.ag_graph WHERE name = @graph_name);", connection)
        {
            CommandTimeout = 5
        };
        graphCommand.Parameters.AddWithValue("graph_name", options.Value.GraphName);
        var graphExists = (bool?)await graphCommand.ExecuteScalarAsync(token) ?? false;
        logger.LogInformation("AGE health check completed in {ElapsedMs}ms (graphExists={GraphExists}).", stopwatch.ElapsedMilliseconds, graphExists);
        return graphExists
            ? Results.Ok(new { status = "ok" })
            : Results.Problem($"AGE graph '{options.Value.GraphName}' not found. Run step 4 to initialize it.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (OperationCanceledException)
    {
        logger.LogWarning("AGE health check timed out after {ElapsedMs}ms.", stopwatch.ElapsedMilliseconds);
        return Results.Problem("AGE health check timed out.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "AGE health check failed after {ElapsedMs}ms.", stopwatch.ElapsedMilliseconds);
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/deployment", () =>
{
    return Results.Ok(new
    {
        dockerComposePath = "Deployment/docker-compose.yml",
        initSqlPath = "Deployment/init/01-age-init.sql",
        notes = "Run docker compose up in the backend project to provision PostgreSQL + Apache AGE."
    });
});

app.Run();

public partial class Program
{
}
