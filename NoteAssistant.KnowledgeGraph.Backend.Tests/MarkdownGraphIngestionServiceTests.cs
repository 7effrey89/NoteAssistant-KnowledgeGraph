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

        var plan = await service.CreateGraphPlanAsync("sample.md", markdown, null, CancellationToken.None);

        Assert.Equal("custom_graph", plan.GraphName);
        Assert.Equal(3, plan.Chunks.Count);
        Assert.All(plan.Chunks, chunk => Assert.NotNull(chunk.Embedding));
        Assert.Equal(3, plan.SqlStatements.Count(s => s.Contains("INSERT INTO chunks", StringComparison.Ordinal)));
    }
}
