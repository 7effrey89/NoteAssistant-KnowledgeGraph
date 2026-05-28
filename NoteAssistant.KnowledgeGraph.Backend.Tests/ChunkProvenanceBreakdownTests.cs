using NoteAssistant.KnowledgeGraph.Backend.Models;
using NoteAssistant.KnowledgeGraph.Backend.Services;
using Xunit;

namespace NoteAssistant.KnowledgeGraph.Backend.Tests;

public class ChunkProvenanceBreakdownTests
{
    [Fact]
    public void BuildChunkSourceBreakdown_ReturnsEmpty_WhenNoChunks()
    {
        var result = AgeGraphRepository.BuildChunkSourceBreakdown([]);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildChunkSourceBreakdown_GroupsSortsAndComputesPercentages()
    {
        var chunks = new[]
        {
            new HybridChunkResultDto(1, 1, 0, "c1", null, Source: "entity"),
            new HybridChunkResultDto(2, 1, 1, "c2", null, Source: "Entity"),
            new HybridChunkResultDto(3, 1, 2, "c3", null, Source: "path-evidence"),
            new HybridChunkResultDto(4, 1, 3, "c4", null, Source: "path-evidence"),
            new HybridChunkResultDto(5, 1, 4, "c5", null, Source: "global"),
            new HybridChunkResultDto(6, 1, 5, "c6", null, Source: null)
        };

        var result = AgeGraphRepository.BuildChunkSourceBreakdown(chunks);

        Assert.Collection(
            result,
            item =>
            {
                Assert.Equal("entity", item.Source);
                Assert.Equal(2, item.Count);
                Assert.Equal(33.3, item.Percentage);
            },
            item =>
            {
                Assert.Equal("path-evidence", item.Source);
                Assert.Equal(2, item.Count);
                Assert.Equal(33.3, item.Percentage);
            },
            item =>
            {
                Assert.Equal("global", item.Source);
                Assert.Equal(1, item.Count);
                Assert.Equal(16.7, item.Percentage);
            },
            item =>
            {
                Assert.Equal("unknown", item.Source);
                Assert.Equal(1, item.Count);
                Assert.Equal(16.7, item.Percentage);
            });
    }

    [Fact]
    public void BuildChunkSourceBreakdown_NormalizesWhitespaceAndCase()
    {
        var chunks = new[]
        {
            new HybridChunkResultDto(1, 1, 0, "c1", null, Source: " entity "),
            new HybridChunkResultDto(2, 1, 1, "c2", null, Source: "ENTITY"),
            new HybridChunkResultDto(3, 1, 2, "c3", null, Source: "\tEntity"),
            new HybridChunkResultDto(4, 1, 3, "c4", null, Source: " "),
            new HybridChunkResultDto(5, 1, 4, "c5", null, Source: null)
        };

        var result = AgeGraphRepository.BuildChunkSourceBreakdown(chunks);

        Assert.Collection(
            result,
            item =>
            {
                Assert.Equal("entity", item.Source);
                Assert.Equal(3, item.Count);
                Assert.Equal(60.0, item.Percentage);
            },
            item =>
            {
                Assert.Equal("unknown", item.Source);
                Assert.Equal(2, item.Count);
                Assert.Equal(40.0, item.Percentage);
            });
    }
}
