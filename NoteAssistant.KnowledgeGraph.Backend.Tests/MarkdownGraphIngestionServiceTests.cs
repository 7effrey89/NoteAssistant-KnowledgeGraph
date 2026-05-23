using Microsoft.Extensions.Options;
using NoteAssistant.KnowledgeGraph.Backend.Models;
using NoteAssistant.KnowledgeGraph.Backend.Services;
using Xunit;

namespace NoteAssistant.KnowledgeGraph.Backend.Tests;

public sealed class MarkdownGraphIngestionServiceTests
{
    [Fact]
    public async Task CreateGraphPlanAsync_UsesFoundryEmbeddingsAndGraphName()
    {
        var embeddings = new List<float[]> { new float[1536], new float[1536], new float[1536] };
        var foundry = new FakeFoundryInferenceClient
        {
            IsConfigured = true,
            Embeddings = embeddings,
            Entities = Array.Empty<EntityDto>()
        };

        var options = Options.Create(new DatabaseOptions
        {
            GraphName = "custom_graph"
        });

        var service = new MarkdownGraphIngestionService(foundry, options);
        var markdown = "# Title\n\nFirst paragraph.\n\nSecond paragraph.";

        var plan = await service.CreateGraphPlanAsync("sample.md", markdown, null, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", CancellationToken.None);

        Assert.Equal("custom_graph", plan.GraphName);
        Assert.Equal(3, plan.Chunks.Count);
        Assert.All(plan.Chunks, chunk => Assert.NotNull(chunk.Embedding));
        Assert.Equal(3, plan.SqlStatements.Count(s => s.Contains("INSERT INTO chunks", StringComparison.Ordinal)));
        Assert.Contains(plan.SqlStatements, s => s.Contains("content_hash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateGraphPlanAsync_UsesStableDocumentId_ForSameContentHash()
    {
        var foundry = new FakeFoundryInferenceClient
        {
            IsConfigured = true,
            Embeddings = [new float[1536]],
            Entities = Array.Empty<EntityDto>()
        };
        var service = new MarkdownGraphIngestionService(foundry, Options.Create(new DatabaseOptions()));
        const string hash = "385acf20f1ee0000000000000000000000000000000000000000000000000000";

        var first = await service.CreateGraphPlanAsync("first.md", "Same content.", null, hash, CancellationToken.None);
        var second = await service.CreateGraphPlanAsync("second.md", "Same content.", null, hash, CancellationToken.None);

        Assert.Equal(first.DocumentId, second.DocumentId);
        Assert.Equal(first.Chunks[0].Id, second.Chunks[0].Id);
    }
}
