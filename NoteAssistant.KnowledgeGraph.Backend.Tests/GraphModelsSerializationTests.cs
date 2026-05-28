using System.Text.Json;
using NoteAssistant.KnowledgeGraph.Backend.Models;
using Xunit;

namespace NoteAssistant.KnowledgeGraph.Backend.Tests;

public class GraphModelsSerializationTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void HybridChunkSourceCountDto_RoundTripsPercentage()
    {
        var dto = new HybridChunkSourceCountDto("entity", 7, 58.3);

        var json = JsonSerializer.Serialize(dto, WebJson);
        var parsed = JsonSerializer.Deserialize<HybridChunkSourceCountDto>(json, WebJson);

        Assert.NotNull(parsed);
        Assert.Equal("entity", parsed.Source);
        Assert.Equal(7, parsed.Count);
        Assert.Equal(58.3, parsed.Percentage);
    }

    [Fact]
    public void HybridChunkSourceCountDto_DeserializesWithoutPercentage_DefaultsToZero()
    {
        const string legacyJson = "{\"source\":\"entity\",\"count\":3}";

        var parsed = JsonSerializer.Deserialize<HybridChunkSourceCountDto>(legacyJson, WebJson);

        Assert.NotNull(parsed);
        Assert.Equal("entity", parsed.Source);
        Assert.Equal(3, parsed.Count);
        Assert.Equal(0d, parsed.Percentage);
    }
}
