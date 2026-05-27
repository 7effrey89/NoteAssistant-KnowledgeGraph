using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NoteAssistant.KnowledgeGraph.Backend.Models;
using NoteAssistant.KnowledgeGraph.Backend.Services;
using Xunit;

namespace NoteAssistant.KnowledgeGraph.Backend.Tests;

public sealed class ApiEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeFoundryInferenceClient _foundry = new()
    {
        IsConfigured = true,
        Embeddings = [new float[1536]]
    };

    public ApiEndpointTests(WebApplicationFactory<Program> factory)
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "kg-tests", Guid.NewGuid().ToString("N"));
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AnalysisCache:Directory"] = cacheDir
                });
            });
            builder.ConfigureServices(services =>
            {
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(IFoundryInferenceClient)
                             || d.ServiceType == typeof(IAgeDatabaseConnectionFactory)
                             || d.ServiceType == typeof(AgeGraphRepository))
                    .ToList();
                foreach (var descriptor in descriptors)
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton<IFoundryInferenceClient>(_foundry);

                services.AddSingleton<IAgeDatabaseConnectionFactory>(new StubAgeDatabaseConnectionFactory
                {
                    IsConfigured = false
                });

                services.AddSingleton<AgeGraphRepository>();
            });
        });
    }

    [Fact]
    public async Task HealthFoundry_ReturnsServiceUnavailable_WhenUnconfigured()
    {
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(IFoundryInferenceClient))
                    .ToList();
                foreach (var descriptor in descriptors)
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton<IFoundryInferenceClient>(new FakeFoundryInferenceClient
                {
                    IsConfigured = false
                });
            });
        }).CreateClient();

        using var response = await client.GetAsync("/api/health/foundry");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task HealthDb_ReturnsServiceUnavailable_WhenUnconfigured()
    {
        var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/health/db");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task HealthAge_ReturnsServiceUnavailable_WhenUnconfigured()
    {
        var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/health/age");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Upload_ReturnsAnalyzed_WhenDbNotConfigured()
    {
        var client = _factory.CreateClient();
        var content = BuildUploadContent("sample.md");

        using var response = await client.PostAsync("/api/documents/upload", content);
        var payload = await response.Content.ReadFromJsonAsync<GraphIngestionPlan>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal("Analyzed", payload!.Status.State);
    }

    [Fact]
    public async Task Ingest_ReturnsReady_WhenDbNotConfigured()
    {
        var client = _factory.CreateClient();
        var content = BuildUploadContent("sample.md");

        using var uploadResponse = await client.PostAsync("/api/documents/upload", content);
        var uploaded = await uploadResponse.Content.ReadFromJsonAsync<GraphIngestionPlan>();
        Assert.NotNull(uploaded);

        using var ingestResponse = await client.PostAsync($"/api/documents/{uploaded!.DocumentId}/ingest", null);
        var ingested = await ingestResponse.Content.ReadFromJsonAsync<GraphIngestionPlan>();

        Assert.Equal(HttpStatusCode.OK, ingestResponse.StatusCode);
        Assert.NotNull(ingested);
        Assert.Equal("Ready", ingested!.Status.State);
    }

    [Fact]
    public async Task Upload_DecomposesMarkdown_IntoChunksEntitiesAndMentions()
    {
        var client = _factory.CreateClient();
        var content = BuildUploadContent("summary.md");

        using var response = await client.PostAsync("/api/documents/upload", content);
        var payload = await response.Content.ReadFromJsonAsync<GraphIngestionPlan>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.True(payload!.DocumentId > 0, "documentId should be assigned");
        Assert.Equal("summary", payload.Title);
        Assert.NotEmpty(payload.Chunks);
        Assert.All(payload.Chunks, c => Assert.False(string.IsNullOrWhiteSpace(c.Text)));
        Assert.NotEmpty(payload.Entities);
        Assert.NotEmpty(payload.Mentions);
        Assert.All(payload.Mentions, m =>
            Assert.Contains(payload.Chunks, c => c.Id == m.ChunkId));
        Assert.NotEmpty(payload.SqlStatements);
        Assert.Equal(payload.DocumentId, payload.Status.DocumentId);
    }

    [Fact]
    public async Task Upload_PersistsStatus_RetrievableViaStatusEndpoint()
    {
        var client = _factory.CreateClient();
        var content = BuildUploadContent("summary.md");

        using var uploadResponse = await client.PostAsync("/api/documents/upload", content);
        var uploaded = await uploadResponse.Content.ReadFromJsonAsync<GraphIngestionPlan>();
        Assert.NotNull(uploaded);

        using var statusResponse = await client.GetAsync($"/api/documents/{uploaded!.DocumentId}/status");
        var status = await statusResponse.Content.ReadFromJsonAsync<IngestionStatusDto>();

        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        Assert.NotNull(status);
        Assert.Equal(uploaded.DocumentId, status!.DocumentId);
        Assert.Equal(uploaded.Status.State, status.State);
    }

    [Fact]
    public async Task BulkMetadata_UpdatesSelectedUploadedPlans()
    {
        var client = _factory.CreateClient();
        using var firstUpload = await client.PostAsync("/api/documents/upload-cache", BuildCacheUploadContent(9001, "first-cache"));
        using var secondUpload = await client.PostAsync("/api/documents/upload-cache", BuildCacheUploadContent(9002, "second-cache"));
        var first = await firstUpload.Content.ReadFromJsonAsync<GraphIngestionPlan>();
        var second = await secondUpload.Content.ReadFromJsonAsync<GraphIngestionPlan>();
        Assert.NotNull(first);
        Assert.NotNull(second);

        var request = new BulkMetadataUpdateRequest(
            [first!.DocumentId],
            "meeting_summary",
            "2026-05-22",
            "bulk, selected");
        using var response = await client.PostAsJsonAsync("/api/documents/metadata", request);
        var payload = await response.Content.ReadFromJsonAsync<BulkMetadataUpdateResponseShape>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        var updated = Assert.Single(payload!.Updated);
        Assert.Equal(first.DocumentId, updated.DocumentId);
        Assert.Equal("meeting_summary", updated.Metadata?.DocumentType);
        Assert.Equal(new DateOnly(2026, 5, 22), updated.Metadata?.DocumentDate);
        Assert.Contains("bulk", updated.Metadata?.Tags ?? []);
        Assert.Contains(updated.SqlStatements, stmt => stmt.Contains("meeting_summary", StringComparison.OrdinalIgnoreCase));

        using var secondIngest = await client.PostAsync($"/api/documents/{second!.DocumentId}/ingest", null);
        var unchanged = await secondIngest.Content.ReadFromJsonAsync<GraphIngestionPlan>();
        Assert.NotNull(unchanged);
        Assert.NotEqual("meeting_summary", unchanged!.Metadata?.DocumentType);
    }

    [Fact]
    public async Task Upload_ReturnsBadRequest_WhenFileMissing()
    {
        var client = _factory.CreateClient();
        var content = new MultipartFormDataContent
        {
            { new StringContent("noop"), "ignored" }
        };

        using var response = await client.PostAsync("/api/documents/upload", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_ReturnsBadRequest_WhenFileIsNotMarkdown()
    {
        var client = _factory.CreateClient();
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("hello"));
        content.Add(fileContent, "file", "notes.txt");

        using var response = await client.PostAsync("/api/documents/upload", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadCache_LoadsPlanWithoutCallingFoundry()
    {
        var client = _factory.CreateClient();
        var plan = new GraphIngestionPlan(
            12345,
            "knowledge_graph",
            "cached-summary",
            new DocumentMetadata("meeting_summary", DateOnly.FromDateTime(DateTime.Today), ["cache"]),
            [new ChunkDto(12345001, 1, "Cached chunk mentions Azure.")],
            [new EntityDto("Platform", "Azure")],
            [new ChunkEntityLinkDto(12345001, "Platform", "Azure")],
            [],
            new IngestionStatusDto(12345, "cached-summary.md", "Analyzed", DateTimeOffset.UtcNow, "Cached analysis."),
            "# Cached summary\n\nCached chunk mentions Azure.",
            "abc123",
            false,
            "cached prompt");

        var json = JsonSerializer.Serialize(plan, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(json));
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Add(fileContent, "file", "cached-summary.json");

        using var response = await client.PostAsync("/api/documents/upload-cache", content);
        var payload = await response.Content.ReadFromJsonAsync<GraphIngestionPlan>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.True(payload!.Cached);
        Assert.Equal("Cached", payload.Status.State);
        Assert.NotEqual(12345, payload.DocumentId);
        Assert.Equal(payload.DocumentId, payload.Status.DocumentId);
        Assert.All(payload.Mentions, mention => Assert.Contains(payload.Chunks, chunk => chunk.Id == mention.ChunkId));
        Assert.Contains("no Foundry call", payload.Status.Message);
        Assert.NotEmpty(payload.SqlStatements);
        Assert.Equal(0, _foundry.CreateEmbeddingsCallCount);
        Assert.Equal(0, _foundry.ExtractEntitiesCallCount);
    }

    [Fact]
    public async Task Upload_RejectsNonMultipartRequests()
    {
        var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/documents/upload", new { content = "hi" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_AllowsCorsFromWebOrigin()
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/documents/upload");
        request.Headers.Add("Origin", "http://localhost:5272");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        using var response = await client.SendAsync(request);

        Assert.True(
            response.Headers.Contains("Access-Control-Allow-Origin"),
            "CORS preflight should expose Access-Control-Allow-Origin so the browser allows uploads from the web app.");
    }

    [Fact]
    public async Task HybridRetrieval_ExplicitPathMode_ReportsResolvedMode_WhenDbIsUnconfigured()
    {
        var client = _factory.CreateClient();
        var request = new HybridRetrievalRequest(
            Query: "How does Microsoft influence OpenAI through product partnerships?",
            RetrievalMode: "path",
            IncludeTrace: true);

        using var response = await client.PostAsJsonAsync("/api/retrieval/hybrid", request);
        var payload = await response.Content.ReadFromJsonAsync<HybridRetrievalResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(payload);
        Assert.False(payload!.Success);
        Assert.Equal("path", payload.ResolvedRetrievalMode);
        Assert.Equal(3, payload.ResolvedTraversalHops);
        Assert.Contains("Explicit mode 'path'", payload.RetrievalModeRationale ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Path", payload.RetrievalOrder, StringComparison.OrdinalIgnoreCase);
        Assert.Null(payload.ChunkSourceBreakdown);
    }

    [Fact]
    public async Task HybridRetrieval_AutoMode_ResolvesPath_ForCausalQuestion()
    {
        var client = _factory.CreateClient();
        var request = new HybridRetrievalRequest(
            Query: "How does Azure depend on OpenAI via platform integrations?",
            RetrievalMode: "auto",
            IncludeTrace: true);

        using var response = await client.PostAsJsonAsync("/api/retrieval/hybrid", request);
        var payload = await response.Content.ReadFromJsonAsync<HybridRetrievalResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal("path", payload!.ResolvedRetrievalMode);
        Assert.Equal(3, payload.ResolvedTraversalHops);
        Assert.Contains("Auto router", payload.RetrievalModeRationale ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Null(payload.ChunkSourceBreakdown);
    }

    [Fact]
    public async Task HybridRetrieval_AutoMode_ResolvesLight_ForSummaryQuestion()
    {
        var client = _factory.CreateClient();
        var request = new HybridRetrievalRequest(
            Query: "Give me a summary of the latest updates.",
            RetrievalMode: "auto",
            IncludeTrace: true);

        using var response = await client.PostAsJsonAsync("/api/retrieval/hybrid", request);
        var payload = await response.Content.ReadFromJsonAsync<HybridRetrievalResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal("light", payload!.ResolvedRetrievalMode);
        Assert.Equal(2, payload.ResolvedTraversalHops);
        Assert.Contains("LIGHT", payload.RetrievalModeRationale ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Null(payload.ChunkSourceBreakdown);
    }

    [Fact]
    public async Task HybridRetrieval_LegacyHybridAlias_MapsToLocalMode()
    {
        var client = _factory.CreateClient();
        var request = new HybridRetrievalRequest(
            Query: "Tell me about Microsoft and OpenAI",
            RetrievalMode: "hybrid",
            IncludeTrace: true);

        using var response = await client.PostAsJsonAsync("/api/retrieval/hybrid", request);
        var payload = await response.Content.ReadFromJsonAsync<HybridRetrievalResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal("local", payload!.ResolvedRetrievalMode);
        Assert.Equal(2, payload.ResolvedTraversalHops);
        Assert.Contains("backward compatibility", payload.RetrievalModeRationale ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Null(payload.ChunkSourceBreakdown);
    }

    [Fact]
    public async Task HybridRetrieval_UnknownMode_FallsBackToAutoRouting()
    {
        var client = _factory.CreateClient();
        var request = new HybridRetrievalRequest(
            Query: "Give me a summary of current status",
            RetrievalMode: "mystery",
            IncludeTrace: true);

        using var response = await client.PostAsJsonAsync("/api/retrieval/hybrid", request);
        var payload = await response.Content.ReadFromJsonAsync<HybridRetrievalResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal("light", payload!.ResolvedRetrievalMode);
        Assert.Equal(2, payload.ResolvedTraversalHops);
        Assert.Contains("Unrecognized requested mode", payload.RetrievalModeRationale ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Null(payload.ChunkSourceBreakdown);
    }

    [Fact]
    public async Task HybridRetrieval_GlobalModeRequest_FallsBackToAutoRouting()
    {
        var client = _factory.CreateClient();
        var request = new HybridRetrievalRequest(
            Query: "Give me a summary of current status",
            RetrievalMode: "global",
            IncludeTrace: true);

        using var response = await client.PostAsJsonAsync("/api/retrieval/hybrid", request);
        var payload = await response.Content.ReadFromJsonAsync<HybridRetrievalResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal("light", payload!.ResolvedRetrievalMode);
        Assert.Equal(2, payload.ResolvedTraversalHops);
        Assert.Contains("Unrecognized requested mode", payload.RetrievalModeRationale ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("global", payload.RetrievalModeRationale ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Null(payload.ChunkSourceBreakdown);
    }

    [Fact]
    public async Task HybridRetrieval_ReturnsChunkSourceBreakdown_WhenRunnerProvidesProvenance()
    {
        var stubResponse = new HybridRetrievalResponse(
            Success: true,
            Error: null,
            DetectedEntities: [],
            GraphEntities: [],
            MatchedEntities: [],
            Chunks:
            [
                new HybridChunkResultDto(1, 10, 0, "Entity-backed chunk", 0.12, Source: "entity"),
                new HybridChunkResultDto(2, 10, 1, "Path evidence chunk", 0.18, Source: "path-evidence")
            ],
            PromptContext: "test-prompt",
            RetrievalOrder: "Entity -> Path evidence",
            ResolvedRetrievalMode: "path",
            RetrievalModeRationale: "stubbed for endpoint contract test",
            ChunkSourceBreakdown:
            [
                new HybridChunkSourceCountDto("entity", 1, 50.0),
                new HybridChunkSourceCountDto("path-evidence", 1, 50.0)
            ],
            ResolvedTraversalHops: 3);

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(IHybridRetrievalRunner))
                    .ToList();
                foreach (var descriptor in descriptors)
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton<IHybridRetrievalRunner>(new StubHybridRetrievalRunner(stubResponse));
            });
        }).CreateClient();

        var request = new HybridRetrievalRequest(
            Query: "show relationship evidence",
            RetrievalMode: "path",
            IncludeTrace: true);

        using var response = await client.PostAsJsonAsync("/api/retrieval/hybrid", request);
        var payload = await response.Content.ReadFromJsonAsync<HybridRetrievalResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.True(payload!.Success);
        Assert.Equal("path", payload.ResolvedRetrievalMode);
        Assert.Equal(3, payload.ResolvedTraversalHops);
        Assert.NotNull(payload.ChunkSourceBreakdown);

        var breakdown = payload.ChunkSourceBreakdown!.ToDictionary(x => x.Source, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, breakdown.Count);
        Assert.Equal(50.0, breakdown["entity"].Percentage);
        Assert.Equal(50.0, breakdown["path-evidence"].Percentage);
    }

    private static MultipartFormDataContent BuildUploadContent(string uploadName)
    {
        var filePath = Path.Combine(AppContext.BaseDirectory, "asset", "summary.md");
        var fileBytes = File.ReadAllBytes(filePath);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/markdown");
        var content = new MultipartFormDataContent
        {
            { fileContent, "file", uploadName }
        };
        return content;
    }

    private static MultipartFormDataContent BuildCacheUploadContent(int documentId, string title)
    {
        var plan = new GraphIngestionPlan(
            documentId,
            "knowledge_graph",
            title,
            new DocumentMetadata("initial", null, [title]),
            [new ChunkDto(documentId * 1000 + 1, 1, $"{title} chunk mentions Azure.")],
            [new EntityDto("Platform", "Azure")],
            [new ChunkEntityLinkDto(documentId * 1000 + 1, "Platform", "Azure")],
            [],
            new IngestionStatusDto(documentId, $"{title}.md", "Analyzed", DateTimeOffset.UtcNow, "Cached analysis."),
            $"# {title}\n\n{title} chunk mentions Azure.",
            $"hash-{documentId}",
            false,
            "cached prompt");

        var json = JsonSerializer.Serialize(plan, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(json));
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Add(fileContent, "file", $"{title}.json");
        return content;
    }

    private sealed record BulkMetadataUpdateResponseShape(IReadOnlyList<GraphIngestionPlan> Updated, IReadOnlyList<long> Missing);

    private sealed class StubHybridRetrievalRunner(HybridRetrievalResponse response) : IHybridRetrievalRunner
    {
        public Task<HybridRetrievalResponse> ExecuteAsync(HybridRetrievalRequest request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    [Fact]
    public async Task AssistQuery_ReturnsSuggestion()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/query/assist", new QueryAssistantRequest("show all entities"));
        var payload = await response.Content.ReadFromJsonAsync<QueryAssistantResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(payload?.SuggestedCypher));
    }

    [Fact]
    public async Task AssistQuery_ReturnsGraphPrimitives_ForExplorer()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/query/assist", new QueryAssistantRequest("show entity mentions"));
        var payload = await response.Content.ReadFromJsonAsync<QueryAssistantResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Contains("RETURN", payload!.SuggestedCypher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("c", payload.SuggestedCypher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("r", payload.SuggestedCypher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("e", payload.SuggestedCypher, StringComparison.OrdinalIgnoreCase);
    }
}
