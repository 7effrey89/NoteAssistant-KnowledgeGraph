using System.Diagnostics;
using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
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
builder.Services.AddSingleton<CommunityProfileStore>();

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

app.MapGet("/api/graph-maker-agent", (IFoundryInferenceClient foundry) => Results.Ok(new
{
    name = "Graph Maker Agent",
    systemPrompt = foundry.EntityExtractionSystemPrompt
}));

var communitySkillsPath = Path.Combine(app.Environment.ContentRootPath, "Prompts", "community_skills.md");
var communitySkillsText = File.Exists(communitySkillsPath)
    ? File.ReadAllText(communitySkillsPath)
    : string.Empty;

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

static DocumentMetadata ParseBulkMetadata(BulkMetadataUpdateRequest request)
{
    var documentType = NormalizeOptional(request.DocumentType);
    var documentDate = ParseDateOnly(request.DocumentDate);
    var tags = ParseTags(request.Tags);

    return new DocumentMetadata(documentType, documentDate, tags);
}

static bool HasMetadataValues(DocumentMetadata metadata)
    => !string.IsNullOrWhiteSpace(metadata.DocumentType)
       || metadata.DocumentDate.HasValue
       || metadata.Tags is { Count: > 0 };

// Used for NoteSession values — no extension stripping, just trim the raw value.
static string NormalizeNoteAssistantSessionKey(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    return value.Trim();
}

// Used for file name fallback — strips the .json extension and the _metadata suffix.
static string NormalizeNoteAssistantFileNameKey(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    // Take just the file name part (handles relative paths like "folder/name.json")
    var fileName = Path.GetFileName(value.Trim());
    var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
    if (withoutExtension.EndsWith("_metadata", StringComparison.OrdinalIgnoreCase))
    {
        withoutExtension = withoutExtension[..^"_metadata".Length];
    }

    return withoutExtension.Trim();
}

static JsonElement BuildNoteAssistantTags(NoteAssistantMetadataFileDto file)
{
    var payload = new
    {
        Customers = file.Customers?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>(),
        Services = file.Services?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>()
    };

    return JsonSerializer.SerializeToElement(payload);
}

static bool HasNoteAssistantTags(JsonElement tags)
{
    if (tags.ValueKind != JsonValueKind.Object)
    {
        return false;
    }

    foreach (var property in tags.EnumerateObject())
    {
        if (property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() > 0)
        {
            return true;
        }
    }

    return false;
}

static DateOnly? ParseNoteAssistantFolderDate(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    return DateTimeOffset.TryParse(value, out var parsed)
        ? DateOnly.FromDateTime(parsed.Date)
        : null;
}

static string ResolveContentHash(GraphIngestionPlan plan, IAnalysisCache cache)
{
    if (!string.IsNullOrWhiteSpace(plan.OriginalContent))
    {
        return cache.ComputeHash(plan.OriginalContent);
    }

    return !string.IsNullOrWhiteSpace(plan.ContentHash)
        ? plan.ContentHash.Trim().ToLowerInvariant()
        : cache.ComputeHash(string.Join("\n\n", plan.Chunks.Select(chunk => chunk.Text)));
}

var cacheJsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

CommunityBuildRequest DefaultCommunityBuildRequest() => new(1, new CommunityDetectionOptions());

CommunityBuildRequest NormalizeCommunityBuildRequest(CommunityBuildRequest request, int? parallelism)
{
    var fallback = DefaultCommunityBuildRequest();
    var detection = request.CommunityDetection ?? fallback.CommunityDetection!;
    return request with
    {
        Parallelism = Math.Clamp(request.Parallelism > 0 ? request.Parallelism : parallelism ?? fallback.Parallelism, 1, 8),
        CommunityDetection = detection with
        {
            Algorithm = string.IsNullOrWhiteSpace(detection.Algorithm) ? "LeidenCpm" : detection.Algorithm.Trim(),
            CpmResolution = Math.Clamp(detection.CpmResolution, 0.001, 2.0),
            TypedRelationshipWeight = Math.Clamp(detection.TypedRelationshipWeight, 0.01, 20.0),
            CoMentionWeight = Math.Clamp(detection.CoMentionWeight, 0.01, 20.0),
            MinCommunitySizeToSummarize = Math.Clamp(detection.MinCommunitySizeToSummarize, 1, 1000),
            MaxCommunitiesToSummarize = Math.Clamp(detection.MaxCommunitiesToSummarize, 1, 500)
        }
    };
}

async Task<CommunityBuildRequest> ResolveCommunityBuildRequestAsync(
    HttpContext httpContext,
    int? parallelism,
    CommunityProfileStore profileStore,
    CancellationToken cancellationToken)
{
    CommunityBuildRequest? request = null;
    if ((httpContext.Request.ContentLength ?? 0) > 0)
    {
        request = await JsonSerializer.DeserializeAsync<CommunityBuildRequest>(httpContext.Request.Body, cacheJsonOptions, cancellationToken);
    }

    if (request is null)
    {
        var activeProfile = await profileStore.GetActiveProfileAsync(cancellationToken);
        request = activeProfile?.Config ?? DefaultCommunityBuildRequest();
    }

    return NormalizeCommunityBuildRequest(request, parallelism);
}

static string? ExtractJsonObject(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    var fencedStart = value.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
    if (fencedStart >= 0)
    {
        var contentStart = value.IndexOf('\n', fencedStart);
        var fencedEnd = contentStart >= 0 ? value.IndexOf("```", contentStart + 1, StringComparison.Ordinal) : -1;
        if (contentStart >= 0 && fencedEnd > contentStart)
        {
            var fencedPayload = value[(contentStart + 1)..fencedEnd].Trim();
            if (fencedPayload.StartsWith("{", StringComparison.Ordinal) && fencedPayload.EndsWith("}", StringComparison.Ordinal))
            {
                return fencedPayload;
            }
        }
    }

    var firstBrace = value.IndexOf('{');
    var lastBrace = value.LastIndexOf('}');
    if (firstBrace >= 0 && lastBrace > firstBrace)
    {
        return value[firstBrace..(lastBrace + 1)];
    }

    return null;
}

static double? ReadPercent(JsonElement root, params string[] names)
{
    foreach (var name in names)
    {
        if (!root.TryGetProperty(name, out var property))
        {
            continue;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var raw))
        {
            return raw <= 1.0 ? Math.Round(raw * 100.0, 2) : Math.Round(raw, 2);
        }

        if (property.ValueKind == JsonValueKind.String && double.TryParse(property.GetString(), out var parsed))
        {
            return parsed <= 1.0 ? Math.Round(parsed * 100.0, 2) : Math.Round(parsed, 2);
        }
    }

    return null;
}

static string? ReadString(JsonElement root, params string[] names)
{
    foreach (var name in names)
    {
        if (root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            var value = property.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
    }

    return null;
}

static CommunityTuningScoreBreakdown ComputeDeterministicCommunityScore(
    CommunityBuildRequest baselineConfig,
    CommunityBuildRequest tunedConfig,
    CommunityTuningAssessmentContext? context)
{
    var scoreComponents = new List<CommunityTuningScoreComponent>();
    var confidenceComponents = new List<CommunityTuningScoreComponent>();

    var baselineDetection = baselineConfig.CommunityDetection ?? new CommunityDetectionOptions();
    var tunedDetection = tunedConfig.CommunityDetection ?? new CommunityDetectionOptions();

    var total = Math.Max(0, context?.TotalCommunities ?? 0);
    var singleton = Math.Max(0, context?.SingletonCommunities ?? 0);
    var multiEntity = Math.Max(0, context?.MultiEntityCommunities ?? 0);
    var candidateSummaryCount = Math.Max(0, context?.CandidateSummaryCount ?? 0);

    void AddScorePenalty(string name, double value, string detail)
    {
        scoreComponents.Add(new CommunityTuningScoreComponent(name, value, detail));
    }

    void AddConfidenceSignal(string name, double value, string detail)
    {
        confidenceComponents.Add(new CommunityTuningScoreComponent(name, value, detail));
    }

    if (total > 0)
    {
        var singletonRatio = Math.Clamp(singleton / (double)total, 0, 1);
        if (singletonRatio >= 0.35)
        {
            AddScorePenalty("fragmentation", 25, $"singleton ratio {singletonRatio:P1} >= 35%");
        }
        else if (singletonRatio >= 0.22)
        {
            AddScorePenalty("fragmentation", 12, $"singleton ratio {singletonRatio:P1} >= 22%");
        }
        else
        {
            AddScorePenalty("fragmentation", 4, $"singleton ratio {singletonRatio:P1}");
        }
    }
    else
    {
        AddScorePenalty("fragmentation", 8, "missing total/singleton community metrics");
    }

    if (multiEntity > 0)
    {
        var coverage = Math.Clamp(candidateSummaryCount / (double)multiEntity, 0, 1);
        if (coverage < 0.5)
        {
            AddScorePenalty("coverage", 22, $"candidate coverage {coverage:P1} < 50%");
        }
        else if (coverage < 0.7)
        {
            AddScorePenalty("coverage", 12, $"candidate coverage {coverage:P1} < 70%");
        }
        else if (coverage < 0.85)
        {
            AddScorePenalty("coverage", 6, $"candidate coverage {coverage:P1} < 85%");
        }
        else
        {
            AddScorePenalty("coverage", 2, $"candidate coverage {coverage:P1}");
        }

        if (multiEntity > tunedDetection.MaxCommunitiesToSummarize)
        {
            var capPressure = (multiEntity - tunedDetection.MaxCommunitiesToSummarize) / (double)multiEntity;
            var capPenalty = 6 + Math.Min(12, Math.Round(capPressure * 24, 2));
            AddScorePenalty("summary-cap", capPenalty, $"multi-entity {multiEntity} exceeds max summaries {tunedDetection.MaxCommunitiesToSummarize}");
        }
    }
    else
    {
        AddScorePenalty("coverage", 7, "missing multi-entity/candidate coverage metrics");
    }

    if (tunedDetection.CpmResolution < 0.08 || tunedDetection.CpmResolution > 0.8)
    {
        AddScorePenalty("resolution-range", 10, $"CPM resolution {tunedDetection.CpmResolution:0.###} is extreme");
    }
    else if (tunedDetection.CpmResolution < 0.12 || tunedDetection.CpmResolution > 0.6)
    {
        AddScorePenalty("resolution-range", 4, $"CPM resolution {tunedDetection.CpmResolution:0.###} is outside preferred range");
    }
    else
    {
        AddScorePenalty("resolution-range", 0, $"CPM resolution {tunedDetection.CpmResolution:0.###} is in preferred range");
    }

    var relationWeightRatio = tunedDetection.CoMentionWeight <= 0
        ? double.PositiveInfinity
        : tunedDetection.TypedRelationshipWeight / tunedDetection.CoMentionWeight;
    if (relationWeightRatio < 0.4 || relationWeightRatio > 4.0)
    {
        AddScorePenalty("weight-balance", 6, $"typed/co-mention ratio {relationWeightRatio:0.##} is extreme");
    }
    else if (relationWeightRatio < 0.7 || relationWeightRatio > 2.8)
    {
        AddScorePenalty("weight-balance", 3, $"typed/co-mention ratio {relationWeightRatio:0.##} is outside preferred range");
    }
    else
    {
        AddScorePenalty("weight-balance", 0, $"typed/co-mention ratio {relationWeightRatio:0.##} is in preferred range");
    }

    if (tunedDetection.MinCommunitySizeToSummarize >= 4)
    {
        AddScorePenalty("min-summary-size", 6, $"min size {tunedDetection.MinCommunitySizeToSummarize} may filter aggressively");
    }
    else if (tunedDetection.MinCommunitySizeToSummarize == 3)
    {
        AddScorePenalty("min-summary-size", 3, "min size is moderately strict");
    }
    else
    {
        AddScorePenalty("min-summary-size", 0, "min size is permissive");
    }

    static double RelativeDelta(double baseline, double tuned)
    {
        var denominator = Math.Max(Math.Abs(baseline), 0.0001);
        return Math.Abs(tuned - baseline) / denominator;
    }

    var cpmDelta = RelativeDelta(baselineDetection.CpmResolution, tunedDetection.CpmResolution);
    var typedDelta = RelativeDelta(baselineDetection.TypedRelationshipWeight, tunedDetection.TypedRelationshipWeight);
    var mentionDelta = RelativeDelta(baselineDetection.CoMentionWeight, tunedDetection.CoMentionWeight);

    var changePenalty = 0.0;
    if (cpmDelta > 0.35) changePenalty += 6;
    else if (cpmDelta > 0.2) changePenalty += 3;

    if (typedDelta > 0.5) changePenalty += 4;
    else if (typedDelta > 0.25) changePenalty += 2;

    if (mentionDelta > 0.5) changePenalty += 4;
    else if (mentionDelta > 0.25) changePenalty += 2;

    if (baselineDetection.Seed != tunedDetection.Seed) changePenalty += 6;
    if (baselineDetection.Directed != tunedDetection.Directed) changePenalty += 5;

    AddScorePenalty("change-magnitude", changePenalty, "penalize overly large or non-reproducible config jumps");

    var totalPenalty = scoreComponents.Sum(component => component.Value);
    var score = Math.Clamp(100.0 - totalPenalty, 0, 100);

    AddConfidenceSignal("base", 35, "baseline confidence");
    AddConfidenceSignal("assessment-context", context is null ? 0 : 10, context is null ? "no assessment context provided" : "assessment context provided");
    AddConfidenceSignal("total-communities", total > 0 ? 15 : 0, total > 0 ? $"total communities={total}" : "missing total communities");
    AddConfidenceSignal("multi-entity", multiEntity > 0 ? 10 : 0, multiEntity > 0 ? $"multi-entity communities={multiEntity}" : "missing multi-entity communities");
    AddConfidenceSignal("candidate-coverage", candidateSummaryCount > 0 ? 8 : 0, candidateSummaryCount > 0 ? $"candidate summaries={candidateSummaryCount}" : "missing candidate summaries");
    AddConfidenceSignal("source-run-metrics", string.Equals(context?.Source, "run-metrics", StringComparison.OrdinalIgnoreCase) ? 7 : 0,
        string.Equals(context?.Source, "run-metrics", StringComparison.OrdinalIgnoreCase) ? "source is run-metrics" : "source is not run-metrics");
    AddConfidenceSignal("sample-size", total >= 80 ? 10 : total >= 30 ? 5 : 0,
        total >= 80 ? "high sample size" : total >= 30 ? "moderate sample size" : "low sample size");

    var confidence = Math.Clamp(confidenceComponents.Sum(component => component.Value), 20, 95);

    return new CommunityTuningScoreBreakdown(
        Method: "deterministic-v1",
        ScorePercent: Math.Round(score, 2),
        ConfidencePercent: Math.Round(confidence, 2),
        ScoreComponents: scoreComponents,
        ConfidenceComponents: confidenceComponents);
}

CommunityBuildRequest? ParseCommunityBuildRequestFromAgentJson(JsonElement root)
{
    CommunityBuildRequest? DeserializeConfigWithAliases(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is not JsonObject obj)
        {
            return null;
        }

        // Some prompts/examples use llmParallelism. Map that alias to the actual request field: parallelism.
        if (!obj.ContainsKey("parallelism") && obj.TryGetPropertyValue("llmParallelism", out var llmParallelismNode))
        {
            obj["parallelism"] = llmParallelismNode?.DeepClone();
        }

        if (obj.TryGetPropertyValue("communityDetection", out var detectionNode) && detectionNode is JsonObject detectionObj)
        {
            if (!obj.ContainsKey("parallelism") && detectionObj.TryGetPropertyValue("llmParallelism", out var nestedParallelism))
            {
                obj["parallelism"] = nestedParallelism?.DeepClone();
            }

            detectionObj.Remove("llmParallelism");
        }

        obj.Remove("llmParallelism");

        var parsed = obj.Deserialize<CommunityBuildRequest>(cacheJsonOptions);
        return parsed is null ? null : NormalizeCommunityBuildRequest(parsed, null);
    }

    if (root.TryGetProperty("config", out var configNode) && configNode.ValueKind == JsonValueKind.Object)
    {
        var fromConfig = DeserializeConfigWithAliases(configNode.GetRawText());
        if (fromConfig is not null)
        {
            return fromConfig;
        }
    }

    if (root.TryGetProperty("updatedConfig", out var updatedConfigNode) && updatedConfigNode.ValueKind == JsonValueKind.Object)
    {
        var fromUpdated = DeserializeConfigWithAliases(updatedConfigNode.GetRawText());
        if (fromUpdated is not null)
        {
            return fromUpdated;
        }
    }

    return DeserializeConfigWithAliases(root.GetRawText());
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
        var identified = ingestionService.ApplyDocumentIdentity(cached, contentHash);
        var refreshed = ingestionService.RefreshSql(identified with { Metadata = metadata });
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

    plan = await ingestionService.CreateGraphPlanAsync(file.FileName, markdown, metadata, contentHash, cancellationToken);
    var analyzed = plan.Status with
    {
        State = "Analyzed",
        Message = "Decomposition ready. Click 'Ingest' to push into PostgreSQL/AGE."
    };
    plan = plan with { Status = analyzed };

    store.Upsert(analyzed);
    store.SavePlan(plan);
    await cache.SaveAsync(plan, cancellationToken);
    return Results.Ok(plan);
})
.DisableAntiforgery();

app.MapPost("/api/documents/upload-cache", async (HttpRequest request, IMarkdownGraphIngestionService ingestionService, IAnalysisCache cache, IngestionStore store, CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Upload must use multipart/form-data." });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files["file"] ?? form.Files.FirstOrDefault();

    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "An analysis cache JSON file is required." });
    }

    if (!file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Only cache analysis .json files are supported." });
    }

    GraphIngestionPlan? imported;
    try
    {
        await using var stream = file.OpenReadStream();
        imported = await JsonSerializer.DeserializeAsync<GraphIngestionPlan>(stream, cacheJsonOptions, cancellationToken);
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid analysis cache JSON: {ex.Message}" });
    }

    if (imported is null)
    {
        return Results.BadRequest(new { error = "Analysis cache JSON was empty or could not be read." });
    }

    if (imported.DocumentId <= 0 || imported.Chunks.Count == 0)
    {
        return Results.BadRequest(new { error = "Analysis cache JSON must contain a documentId and at least one chunk." });
    }

    var contentHash = ResolveContentHash(imported, cache);
    var identified = ingestionService.ApplyDocumentIdentity(imported, contentHash);

    var metadata = ParseMetadata(form);
    var metadataToUse = HasMetadataValues(metadata) ? metadata : identified.Metadata;
    var fileName = string.IsNullOrWhiteSpace(identified.Status.FileName)
        ? Path.ChangeExtension(file.FileName, ".md")
        : identified.Status.FileName;

    var refreshed = ingestionService.RefreshSql(identified with { Metadata = metadataToUse });
    var status = refreshed.Status with
    {
        FileName = fileName,
        State = "Cached",
        UpdatedAt = DateTimeOffset.UtcNow,
        Message = "Loaded from uploaded analysis cache JSON (no Foundry call). Click 'Ingest' to push into PostgreSQL/AGE."
    };
    var plan = refreshed with
    {
        Cached = true,
        Metadata = metadataToUse,
        Status = status
    };

    store.Upsert(status);
    store.SavePlan(plan);
    return Results.Ok(plan);
})
.DisableAntiforgery();

app.MapPost("/api/documents/metadata", (BulkMetadataUpdateRequest request, IMarkdownGraphIngestionService ingestionService, IngestionStore store) =>
{
    if (request.DocumentIds is null || request.DocumentIds.Count == 0)
    {
        return Results.BadRequest(new { error = "At least one documentId is required." });
    }

    var metadata = ParseBulkMetadata(request);
    if (!HasMetadataValues(metadata))
    {
        return Results.BadRequest(new { error = "At least one metadata field is required." });
    }

    var updated = new List<GraphIngestionPlan>();
    var missing = new List<long>();
    foreach (var documentId in request.DocumentIds.Distinct())
    {
        var plan = store.GetPlan(documentId);
        if (plan is null)
        {
            missing.Add(documentId);
            continue;
        }

        var refreshed = ingestionService.RefreshSql(plan with { Metadata = metadata });
        var status = refreshed.Status with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Message = "Metadata updated. Click 'Ingest' to push into PostgreSQL/AGE."
        };
        var updatedPlan = refreshed with { Metadata = metadata, Status = status };
        store.Upsert(status);
        store.SavePlan(updatedPlan);
        updated.Add(updatedPlan);
    }

    if (updated.Count == 0)
    {
        return Results.NotFound(new { error = "No uploaded plans were found for the provided documentIds.", missing });
    }

    return Results.Ok(new { updated, missing });
});

app.MapPost("/api/documents/{documentId:long}/decompose", async (long documentId, DocumentDecomposeRequest? request, IMarkdownGraphIngestionService ingestionService, IAnalysisCache cache, IngestionStore store, CancellationToken cancellationToken) =>
{
    var existing = store.GetPlan(documentId);
    if (existing is null)
    {
        return Results.NotFound(new { error = "Document not found. Upload it first." });
    }

    if (string.IsNullOrWhiteSpace(existing.OriginalContent))
    {
        return Results.BadRequest(new { error = "Original markdown content is missing for this document. Re-upload the markdown file and try again." });
    }

    var requestedMetadata = request is null
        ? new DocumentMetadata(null, null, Array.Empty<string>())
        : new DocumentMetadata(NormalizeOptional(request.DocumentType), ParseDateOnly(request.DocumentDate), ParseTags(request.Tags));
    var metadataToUse = HasMetadataValues(requestedMetadata)
        ? requestedMetadata
        : (existing.Metadata ?? requestedMetadata);

    var fileName = string.IsNullOrWhiteSpace(existing.Status.FileName)
        ? $"{existing.Title}.md"
        : existing.Status.FileName;
    var contentHash = ResolveContentHash(existing, cache);
    var rebuilt = await ingestionService.CreateGraphPlanAsync(fileName, existing.OriginalContent, metadataToUse, contentHash, cancellationToken);
    var status = rebuilt.Status with
    {
        FileName = fileName,
        State = "Analyzed",
        UpdatedAt = DateTimeOffset.UtcNow,
        Message = "Document decomposed with current metadata. Click 'Ingest' to push into PostgreSQL/AGE."
    };

    var updated = rebuilt with
    {
        Cached = false,
        Status = status
    };

    store.Upsert(status);
    store.SavePlan(updated);
    return Results.Ok(updated);
})
.DisableAntiforgery();

app.MapPost("/api/noteassistant/import-metadata", async (NoteAssistantMetadataImportRequest request, IAgeDatabaseConnectionFactory connectionFactory, IOptions<DatabaseOptions> databaseOptions, CancellationToken cancellationToken) =>
{
    if (request.Files is null || request.Files.Count == 0)
    {
        return Results.BadRequest(new { error = "At least one metadata file is required." });
    }

    if (!connectionFactory.IsConfigured)
    {
        return Results.BadRequest(new { error = "ConnectionStrings:AgeDatabase is not configured." });
    }

    var schemaName = NormalizeSchemaName(databaseOptions.Value.SchemaName);
    var results = new List<object>();
    var updatedFileCount = 0;
    var notFoundFileCount = 0;
    await using var connection = await connectionFactory.OpenAsync(cancellationToken);

    const string sql = """
WITH matches AS (
    SELECT id,
           title,
           file_name,
           document_type,
           document_date,
           tags
    FROM kg_data.documents
    WHERE lower(regexp_replace(title, '_[^_]*$', '')) = lower(@matchKey)
),
updated AS (
    UPDATE kg_data.documents AS documents
    SET document_type = COALESCE(documents.document_type, @documentType),
        document_date = COALESCE(documents.document_date, @documentDate),
        tags = COALESCE(documents.tags, CAST(@tagsJson AS jsonb))
    FROM matches
    WHERE documents.id = matches.id
    RETURNING documents.id,
              documents.title,
              documents.file_name,
              matches.document_type IS NULL AS updated_document_type,
              matches.document_date IS NULL AS updated_document_date,
              matches.tags IS NULL AS updated_tags
)
SELECT json_build_object(
    'matchedCount', (SELECT COUNT(*) FROM matches),
    'updatedCount', (SELECT COUNT(*) FROM updated),
    'updatedRows', COALESCE((SELECT json_agg(updated) FROM updated), '[]'::json),
    'matchedRows', COALESCE((SELECT json_agg(matches) FROM matches), '[]'::json)
);
""";

    await using var command = new NpgsqlCommand(sql.Replace("kg_data", schemaName), connection);

    foreach (var file in request.Files)
    {
        var matchKey = NormalizeNoteAssistantSessionKey(file.NoteSession);
        if (string.IsNullOrWhiteSpace(matchKey))
        {
            matchKey = NormalizeNoteAssistantFileNameKey(file.FileName);
        }

        var documentDate = ParseNoteAssistantFolderDate(file.FolderCreationDate);
        var tags = BuildNoteAssistantTags(file);

        command.Parameters.Clear();
        command.Parameters.AddWithValue("matchKey", NpgsqlDbType.Text, matchKey);
        command.Parameters.AddWithValue("documentType", NpgsqlDbType.Text, "meeting_summary");
        var documentDateParameter = command.Parameters.Add("documentDate", NpgsqlDbType.Date);
        documentDateParameter.Value = documentDate.HasValue ? documentDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        command.Parameters.AddWithValue("tagsJson", NpgsqlDbType.Jsonb, tags.GetRawText());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        object? payload = null;
        if (await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0))
        {
            payload = JsonSerializer.Deserialize<JsonElement>(reader.GetString(0));
        }
        await reader.CloseAsync();

        var summary = payload is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(new { matchedCount = 0, updatedCount = 0, updatedRows = Array.Empty<object>(), matchedRows = Array.Empty<object>() });

        var matchedCount = summary.TryGetProperty("matchedCount", out var matchedValue) ? matchedValue.GetInt32() : 0;
        var updatedCount = summary.TryGetProperty("updatedCount", out var updatedValue) ? updatedValue.GetInt32() : 0;

        var status = matchedCount == 0 ? "not-found" : updatedCount == 0 ? "already-populated" : "updated";
        if (status == "updated")
        {
            updatedFileCount++;
        }
        else if (status == "not-found")
        {
            notFoundFileCount++;
        }

        results.Add(new
        {
            fileName = file.FileName,
            noteSession = file.NoteSession,
            matchKey,
            documentType = "meeting_summary",
            documentDate = documentDate?.ToString("yyyy-MM-dd"),
            tags,
            hasTags = HasNoteAssistantTags(tags),
            matchedCount,
            updatedCount,
            detail = summary,
            status
        });
    }

    return Results.Ok(new
    {
        processed = results.Count,
        updated = updatedFileCount,
        notFound = notFoundFileCount,
        results
    });
});

app.MapPost("/api/documents/{documentId:long}/ingest", async (long documentId, IngestionStore store, AgeGraphRepository repository, CancellationToken cancellationToken) =>
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

app.MapGet("/api/documents/{documentId:long}/ingest/preview", (long documentId, IngestionStore store) =>
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

app.MapGet("/api/documents/{documentId:long}/ingest/log", (long documentId, IngestionStore store) =>
{
    var log = store.GetExecutionLog(documentId);
    return log is null
        ? Results.NotFound(new { error = $"No execution log found for document {documentId}." })
        : Results.Ok(log);
});

app.MapPost("/api/documents/{documentId:long}/graph", async (long documentId, IngestionStore store, AgeGraphRepository repository, IOptions<DatabaseOptions> options, CancellationToken cancellationToken) =>
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

app.MapGet("/api/documents/{documentId:long}/status", (long documentId, IngestionStore store) =>
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

app.MapPost("/api/graph/node/details", async (GraphNodeDetailsRequest request, AgeGraphRepository repository, CancellationToken cancellationToken) =>
{
    var response = await repository.GetNodeDetailsAsync(request, cancellationToken);
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

app.MapPost("/api/communities/build", async (HttpContext httpContext, int? parallelism, AgeGraphRepository repository, CommunityProfileStore profileStore, CancellationToken cancellationToken) =>
{
    var request = await ResolveCommunityBuildRequestAsync(httpContext, parallelism, profileStore, cancellationToken);
    var response = await repository.BuildCommunitiesAsync(
        includeTrace: true,
        cancellationToken,
        maxParallelism: Math.Clamp(request.Parallelism, 1, 8),
        communityDetection: request.CommunityDetection);
    return response.Success ? Results.Ok(response) : Results.BadRequest(response);
});

app.MapPost("/api/communities/build/stream", async (HttpContext httpContext, int? parallelism, AgeGraphRepository repository, CommunityProfileStore profileStore, CancellationToken cancellationToken) =>
{
    var request = await ResolveCommunityBuildRequestAsync(httpContext, parallelism, profileStore, cancellationToken);
    httpContext.Response.ContentType = "application/x-ndjson";
    httpContext.Response.Headers.CacheControl = "no-cache";
    var writeLock = new SemaphoreSlim(1, 1);

    async Task WriteEventAsync(string type, object payload, CancellationToken token)
    {
        await writeLock.WaitAsync(token);
        try
        {
            await JsonSerializer.SerializeAsync(httpContext.Response.Body, new { type, payload }, cacheJsonOptions, token);
            await httpContext.Response.WriteAsync("\n", token);
            await httpContext.Response.Body.FlushAsync(token);
        }
        finally
        {
            writeLock.Release();
        }
    }

    var response = await repository.BuildCommunitiesAsync(
        includeTrace: true,
        cancellationToken,
        async (step, token) => await WriteEventAsync("step", step, token),
        async (name, summary, token) => await WriteEventAsync("step-start", new { name, summary }, token),
        Math.Clamp(request.Parallelism, 1, 8),
        request.CommunityDetection);

    await WriteEventAsync("complete", response, cancellationToken);
});

app.MapPost("/api/communities/detect/stream", async (HttpContext httpContext, int? parallelism, AgeGraphRepository repository, CommunityProfileStore profileStore, CancellationToken cancellationToken) =>
{
    var request = await ResolveCommunityBuildRequestAsync(httpContext, parallelism, profileStore, cancellationToken);
    httpContext.Response.ContentType = "application/x-ndjson";
    httpContext.Response.Headers.CacheControl = "no-cache";
    var writeLock = new SemaphoreSlim(1, 1);

    async Task WriteEventAsync(string type, object payload, CancellationToken token)
    {
        await writeLock.WaitAsync(token);
        try
        {
            await JsonSerializer.SerializeAsync(httpContext.Response.Body, new { type, payload }, cacheJsonOptions, token);
            await httpContext.Response.WriteAsync("\n", token);
            await httpContext.Response.Body.FlushAsync(token);
        }
        finally
        {
            writeLock.Release();
        }
    }

    var response = await repository.BuildCommunitiesAsync(
        includeTrace: true,
        cancellationToken,
        async (step, token) => await WriteEventAsync("step", step, token),
        async (name, summary, token) => await WriteEventAsync("step-start", new { name, summary }, token),
        Math.Clamp(request.Parallelism, 1, 8),
        request.CommunityDetection,
        stopAfterClustering: true);

    await WriteEventAsync("gate", new
    {
        message = "Leiden community detection is complete. Review the detected communities before clearing the existing community index or running LLM summaries.",
        response
    }, cancellationToken);
    await WriteEventAsync("complete", response, cancellationToken);
});

app.MapGet("/api/communities/profiles", async (CommunityProfileStore profileStore, CancellationToken cancellationToken) =>
{
    var snapshot = await profileStore.GetSnapshotAsync(cancellationToken);
    return Results.Ok(new
    {
        snapshot.ActiveProfileId,
        snapshot.Profiles,
        defaultConfig = DefaultCommunityBuildRequest()
    });
});

app.MapPost("/api/communities/profiles", async (SaveCommunityProfileRequest request, CommunityProfileStore profileStore, CancellationToken cancellationToken) =>
{
    var normalizedConfig = NormalizeCommunityBuildRequest(request.Config ?? DefaultCommunityBuildRequest(), null);
    var profile = await profileStore.SaveProfileAsync(request with { Config = normalizedConfig }, cancellationToken);
    var snapshot = await profileStore.GetSnapshotAsync(cancellationToken);
    return Results.Ok(new
    {
        profile,
        snapshot.ActiveProfileId,
        snapshot.Profiles
    });
});

app.MapPost("/api/communities/profiles/active", async (SetActiveCommunityProfileRequest request, CommunityProfileStore profileStore, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.ProfileId))
    {
        return Results.BadRequest(new { error = "ProfileId is required." });
    }

    try
    {
        var snapshot = await profileStore.SetActiveProfileAsync(request.ProfileId.Trim(), cancellationToken);
        return Results.Ok(snapshot);
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

app.MapDelete("/api/communities/profiles/{profileId}", async (string profileId, CommunityProfileStore profileStore, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(profileId))
    {
        return Results.BadRequest(new { error = "Profile id is required." });
    }

    try
    {
        var snapshot = await profileStore.DeleteProfileAsync(profileId.Trim(), cancellationToken);
        return Results.Ok(snapshot);
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

app.MapPost("/api/communities/tune-profile", async (TuneCommunityProfileRequest request, IFoundryInferenceClient foundry, CommunityProfileStore profileStore, CancellationToken cancellationToken) =>
{
    if (!foundry.IsConfigured)
    {
        return Results.BadRequest(new CommunityTuningAgentResponse(false, "Foundry is not configured.", null));
    }

    var currentConfig = NormalizeCommunityBuildRequest(request.CurrentConfig ?? DefaultCommunityBuildRequest(), null);
    var systemPromptBuilder = new StringBuilder();
    systemPromptBuilder.AppendLine(string.IsNullOrWhiteSpace(request.SystemPrompt)
        ? "You are GraphRag Community Tuning Agent."
        : request.SystemPrompt.Trim());
    systemPromptBuilder.AppendLine();
    systemPromptBuilder.AppendLine("Follow this skill guide when evaluating and tuning:");
    systemPromptBuilder.AppendLine();
    systemPromptBuilder.AppendLine(string.IsNullOrWhiteSpace(communitySkillsText)
        ? "(community_skills.md not found)"
        : communitySkillsText.Trim());
    systemPromptBuilder.AppendLine();
    systemPromptBuilder.AppendLine("Return ONLY JSON with this shape:");
    systemPromptBuilder.AppendLine("{");
    systemPromptBuilder.AppendLine("  \"config\": { ...full CommunityBuildRequest... },");
    systemPromptBuilder.AppendLine("  \"scorePercent\": 0-100,");
    systemPromptBuilder.AppendLine("  \"confidencePercent\": 0-100,");
    systemPromptBuilder.AppendLine("  \"improvement\": \"short explanation of how tuning changes quality\"");
    systemPromptBuilder.AppendLine("}");

    var effectiveUserPrompt = string.IsNullOrWhiteSpace(request.UserPrompt)
        ? $"Assess and tune this community config:\n```json\n{JsonSerializer.Serialize(currentConfig, cacheJsonOptions)}\n```"
        : request.UserPrompt.Trim();

    var completion = await foundry.CompletePromptAsync(
        systemPromptBuilder.ToString(),
        effectiveUserPrompt,
        agentName: "GraphRag Community Tuning Agent",
        operation: "tune-community-profile",
        cancellationToken);

    var jsonPayload = ExtractJsonObject(completion.Content);
    if (string.IsNullOrWhiteSpace(jsonPayload))
    {
        var failure = new CommunityTuningAgentResponse(
            Success: false,
            Error: "GraphRag Community Tuning Agent did not return JSON.",
            Config: null,
            AgentResponse: completion.Content,
            TokenUsage: completion.TokenUsage is null ? null : new HybridTokenUsageDto(completion.TokenUsage.PromptTokens, completion.TokenUsage.CompletionTokens));
        return Results.BadRequest(failure);
    }

    CommunityBuildRequest? parsedConfig;
    double? scorePercent;
    double? confidencePercent;
    string? improvement;
    try
    {
        using var document = JsonDocument.Parse(jsonPayload);
        parsedConfig = ParseCommunityBuildRequestFromAgentJson(document.RootElement) ?? currentConfig;
        scorePercent = ReadPercent(document.RootElement, "scorePercent", "score", "qualityScore", "qualityPercent");
        confidencePercent = ReadPercent(document.RootElement, "confidencePercent", "confidence", "confidenceScore");
        improvement = ReadString(document.RootElement, "improvement", "improvementSummary", "rationale", "diagnosis");
    }
    catch (JsonException)
    {
        var failure = new CommunityTuningAgentResponse(
            Success: false,
            Error: "GraphRag Community Tuning Agent returned invalid JSON.",
            Config: null,
            AgentResponse: completion.Content,
            TokenUsage: completion.TokenUsage is null ? null : new HybridTokenUsageDto(completion.TokenUsage.PromptTokens, completion.TokenUsage.CompletionTokens));
        return Results.BadRequest(failure);
    }

    parsedConfig = NormalizeCommunityBuildRequest(parsedConfig ?? currentConfig, null);
    var scoreBreakdown = ComputeDeterministicCommunityScore(currentConfig, parsedConfig, request.AssessmentContext);
    scorePercent = scoreBreakdown.ScorePercent;
    confidencePercent = scoreBreakdown.ConfidencePercent;

    CommunityTuningProfile? savedProfile = null;
    if (request.PersistProfile)
    {
        var snapshot = await profileStore.GetSnapshotAsync(cancellationToken);
        var matchingProfile = snapshot.Profiles
            .FirstOrDefault(profile => NormalizeCommunityBuildRequest(profile.Config, null) == parsedConfig);

        if (matchingProfile is not null)
        {
            // Reuse existing persisted profile to keep identical configs from diverging in displayed score/confidence.
            scorePercent = matchingProfile.ScorePercent ?? scorePercent;
            confidencePercent = matchingProfile.ConfidencePercent ?? confidencePercent;
            improvement ??= matchingProfile.Improvement;
            savedProfile = matchingProfile;

            if (!string.Equals(snapshot.ActiveProfileId, matchingProfile.Id, StringComparison.Ordinal))
            {
                var updated = await profileStore.SetActiveProfileAsync(matchingProfile.Id, cancellationToken);
                savedProfile = updated.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, matchingProfile.Id, StringComparison.Ordinal)) ?? matchingProfile;
            }
        }
        else
        {
            savedProfile = await profileStore.SaveProfileAsync(
                new SaveCommunityProfileRequest(
                    Config: parsedConfig,
                    ScorePercent: scorePercent,
                    ConfidencePercent: confidencePercent,
                    Improvement: improvement,
                    Source: "GraphRag Community Tuning Agent",
                    MakeActive: true),
                cancellationToken);
        }
    }

    var response = new CommunityTuningAgentResponse(
        Success: true,
        Error: null,
        Config: parsedConfig,
        ScorePercent: scorePercent,
        ConfidencePercent: confidencePercent,
        Improvement: improvement,
        AgentResponse: completion.Content,
        TokenUsage: completion.TokenUsage is null ? null : new HybridTokenUsageDto(completion.TokenUsage.PromptTokens, completion.TokenUsage.CompletionTokens),
        SavedProfile: savedProfile,
        ScoreBreakdown: scoreBreakdown);

    return Results.Ok(response);
});

app.MapPost("/api/retrieval/global", async (GlobalGraphRagRequest request, AgeGraphRepository repository, CancellationToken cancellationToken) =>
{
    var response = await repository.ExecuteGlobalGraphRagAsync(request, cancellationToken);
    return response.Success ? Results.Ok(response) : Results.BadRequest(response);
});

app.MapGet("/api/tokens/stats", async (int? days, IAgeDatabaseConnectionFactory connectionFactory, CancellationToken cancellationToken) =>
{
    if (!connectionFactory.IsConfigured)
    {
        return Results.Problem("Database settings are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var lookbackDays = Math.Clamp(days ?? 30, 1, 365);
    await using var connection = await connectionFactory.OpenAsync(cancellationToken);
    await EnsureTokenUsageTableAsync(connection, cancellationToken);

    var since = DateTimeOffset.UtcNow.AddDays(-lookbackDays);
    const string summarySql = """
                              SELECT COUNT(*)::bigint AS calls,
                                                   COALESCE(SUM(prompt_tokens), 0)::bigint AS prompt_tokens,
                                                   COALESCE(SUM(completion_tokens), 0)::bigint AS completion_tokens,
                                     COALESCE(SUM(total_tokens), 0)::bigint AS total_tokens
                              FROM "global".llm_token_usage
                              WHERE occurred_at >= @since;
                              """;
    await using var summaryCommand = new NpgsqlCommand(summarySql, connection);
    summaryCommand.Parameters.AddWithValue("since", since);
    await using var summaryReader = await summaryCommand.ExecuteReaderAsync(cancellationToken);
    await summaryReader.ReadAsync(cancellationToken);
    var summary = new
    {
        calls = summaryReader.GetInt64(0),
        promptTokens = summaryReader.GetInt64(1),
        completionTokens = summaryReader.GetInt64(2),
        totalTokens = summaryReader.GetInt64(3)
    };
    await summaryReader.CloseAsync();

    const string byAgentSql = """
                              SELECT agent,
                                     operation,
                                     COUNT(*)::bigint AS calls,
                                     COALESCE(SUM(prompt_tokens), 0)::bigint AS prompt_tokens,
                                     COALESCE(SUM(completion_tokens), 0)::bigint AS completion_tokens,
                                     COALESCE(SUM(total_tokens), 0)::bigint AS total_tokens,
                                     MAX(occurred_at) AS last_seen
                              FROM "global".llm_token_usage
                              WHERE occurred_at >= @since
                              GROUP BY agent, operation
                              ORDER BY total_tokens DESC, calls DESC, agent, operation;
                              """;
    var byAgent = new List<object>();
    await using (var command = new NpgsqlCommand(byAgentSql, connection))
    {
        command.Parameters.AddWithValue("since", since);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            byAgent.Add(new
            {
                agent = reader.GetString(0),
                operation = reader.GetString(1),
                calls = reader.GetInt64(2),
                promptTokens = reader.GetInt64(3),
                completionTokens = reader.GetInt64(4),
                totalTokens = reader.GetInt64(5),
                lastSeen = reader.GetDateTime(6)
            });
        }
    }

        const string dailySql = @"
                    SELECT date_trunc('day', occurred_at) AS day,
                        COUNT(*)::bigint AS calls,
                        COALESCE(SUM(prompt_tokens), 0)::bigint AS prompt_tokens,
                        COALESCE(SUM(completion_tokens), 0)::bigint AS completion_tokens,
                        COALESCE(SUM(total_tokens), 0)::bigint AS total_tokens
                    FROM ""global"".llm_token_usage
                    WHERE occurred_at >= @since
                    GROUP BY day
                    ORDER BY day;
                    ";
    var daily = new List<object>();
    await using (var command = new NpgsqlCommand(dailySql, connection))
    {
        command.Parameters.AddWithValue("since", since);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            daily.Add(new
            {
                day = reader.GetDateTime(0),
                calls = reader.GetInt64(1),
                promptTokens = reader.GetInt64(2),
                completionTokens = reader.GetInt64(3),
                totalTokens = reader.GetInt64(4)
            });
        }
    }

    const string recentSql = """
                             SELECT occurred_at, agent, operation, model_deployment, prompt_tokens, completion_tokens, total_tokens
                             FROM "global".llm_token_usage
                             WHERE occurred_at >= @since
                             ORDER BY occurred_at DESC
                             LIMIT 100;
                             """;
    var recent = new List<object>();
    await using (var command = new NpgsqlCommand(recentSql, connection))
    {
        command.Parameters.AddWithValue("since", since);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            recent.Add(new
            {
                occurredAt = reader.GetDateTime(0),
                agent = reader.GetString(1),
                operation = reader.GetString(2),
                modelDeployment = reader.IsDBNull(3) ? null : reader.GetString(3),
                promptTokens = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                completionTokens = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                totalTokens = reader.IsDBNull(6) ? 0 : reader.GetInt32(6)
            });
        }
    }

    return Results.Ok(new { days = lookbackDays, summary, byAgent, daily, recent });
});

app.MapGet("/api/tokens/pricing", async (CancellationToken cancellationToken) =>
{
    var rows = await LoadTokenPricingRowsAsync(cancellationToken);
    return Results.Ok(new { rows });
});

app.MapPost("/api/tokens/pricing", async (TokenPricingUpdateRequest? request, CancellationToken cancellationToken) =>
{
    var candidateRows = request?.Rows ?? Array.Empty<TokenPricingRow>();
    var normalized = candidateRows
        .Select(NormalizeTokenPricingRow)
        .Where(row => !string.IsNullOrWhiteSpace(row.Region) && !string.IsNullOrWhiteSpace(row.Model) && !string.IsNullOrWhiteSpace(row.Currency))
        .ToList();

    if (normalized.Count == 0)
    {
        normalized = GetDefaultTokenPricingRows().ToList();
    }

    await SaveTokenPricingRowsAsync(normalized, cancellationToken);
    return Results.Ok(new { rows = normalized });
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

static async Task EnsureTokenUsageTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
{
    const string sql = """
                       CREATE SCHEMA IF NOT EXISTS "global";
                       CREATE TABLE IF NOT EXISTS "global".llm_token_usage (
                           id BIGSERIAL PRIMARY KEY,
                           occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                           agent TEXT NOT NULL,
                           operation TEXT NOT NULL,
                           model_deployment TEXT NULL,
                           prompt_tokens INTEGER NULL,
                           completion_tokens INTEGER NULL,
                           total_tokens INTEGER NULL
                       );
                       CREATE INDEX IF NOT EXISTS idx_llm_token_usage_occurred_at ON "global".llm_token_usage(occurred_at);
                       CREATE INDEX IF NOT EXISTS idx_llm_token_usage_agent ON "global".llm_token_usage(agent);
                       """;
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static string GetTokenPricingFilePath()
{
    var baseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoteAssistant.KnowledgeGraph");
    return Path.Combine(baseDir, "token-pricing.json");
}

static IReadOnlyList<TokenPricingRow> GetDefaultTokenPricingRows()
{
    return
    [
        new TokenPricingRow(
            Id: "sweden-central-gpt-5-4",
            Region: "Sweden Central",
            Model: "GPT-5.4 (<272k context) Global",
            Currency: "DKK",
            InputPerMillion: 15.97m,
            CachedInputPerMillion: 1.60m,
            OutputPerMillion: 59.79m)
    ];
}

static TokenPricingRow NormalizeTokenPricingRow(TokenPricingRow row)
{
    var id = string.IsNullOrWhiteSpace(row.Id) ? Guid.NewGuid().ToString("N") : row.Id.Trim();
    var region = row.Region?.Trim() ?? string.Empty;
    var model = row.Model?.Trim() ?? string.Empty;
    var currency = row.Currency?.Trim().ToUpperInvariant() ?? string.Empty;

    return row with
    {
        Id = id,
        Region = region,
        Model = model,
        Currency = currency,
        InputPerMillion = Math.Max(0, row.InputPerMillion),
        CachedInputPerMillion = Math.Max(0, row.CachedInputPerMillion),
        OutputPerMillion = Math.Max(0, row.OutputPerMillion)
    };
}

static async Task<IReadOnlyList<TokenPricingRow>> LoadTokenPricingRowsAsync(CancellationToken cancellationToken)
{
    var path = GetTokenPricingFilePath();
    if (!File.Exists(path))
    {
        var defaults = GetDefaultTokenPricingRows();
        await SaveTokenPricingRowsAsync(defaults, cancellationToken);
        return defaults;
    }

    try
    {
        await using var stream = File.OpenRead(path);
        var payload = await JsonSerializer.DeserializeAsync<TokenPricingFilePayload>(stream, cancellationToken: cancellationToken);
        var rows = payload?.Rows?
            .Select(NormalizeTokenPricingRow)
            .Where(row => !string.IsNullOrWhiteSpace(row.Region) && !string.IsNullOrWhiteSpace(row.Model) && !string.IsNullOrWhiteSpace(row.Currency))
            .ToList();

        if (rows is { Count: > 0 })
        {
            return rows;
        }
    }
    catch
    {
    }

    var fallback = GetDefaultTokenPricingRows();
    await SaveTokenPricingRowsAsync(fallback, cancellationToken);
    return fallback;
}

static async Task SaveTokenPricingRowsAsync(IEnumerable<TokenPricingRow> rows, CancellationToken cancellationToken)
{
    var path = GetTokenPricingFilePath();
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var payload = new TokenPricingFilePayload(rows.ToList());
    var options = new JsonSerializerOptions
    {
        WriteIndented = true
    };

    await using var stream = File.Create(path);
    await JsonSerializer.SerializeAsync(stream, payload, options, cancellationToken);
}

app.Run();

public sealed record TokenPricingRow(
    string Id,
    string Region,
    string Model,
    string Currency,
    decimal InputPerMillion,
    decimal CachedInputPerMillion,
    decimal OutputPerMillion);

public sealed record TokenPricingUpdateRequest(IReadOnlyList<TokenPricingRow>? Rows);

public sealed record TokenPricingFilePayload(IReadOnlyList<TokenPricingRow> Rows);

public partial class Program
{
}
