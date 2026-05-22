using System.Net;
using System.Net.Http.Json;
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

                services.AddSingleton<IFoundryInferenceClient>(new FakeFoundryInferenceClient
                {
                    IsConfigured = true,
                    Embeddings = [new float[1536]]
                });

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
