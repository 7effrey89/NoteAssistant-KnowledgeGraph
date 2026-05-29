using NoteAssistant.KnowledgeGraph.Backend.Services;
using Xunit;

namespace NoteAssistant.KnowledgeGraph.Backend.Tests;

public sealed class QueryAssistantServiceTests
{
    [Fact]
    public void Suggest_UsesGraphPrimitives_ForDocumentChunks()
    {
        var service = new QueryAssistantService();

        var response = service.Suggest("show document chunks");

        Assert.Contains("RETURN d, r, c", response.SuggestedCypher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HAS_CHUNK", response.SuggestedCypher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Suggest_UsesGraphPrimitives_ForEntityMentions()
    {
        var service = new QueryAssistantService();

        var response = service.Suggest("list entity mentions");

        Assert.Contains("RETURN c, r, e", response.SuggestedCypher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MENTIONS", response.SuggestedCypher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Suggest_FallsBackToExplorerQuery()
    {
        var service = new QueryAssistantService();

        var response = service.Suggest("just explore");

        Assert.Contains("RETURN n, r, m", response.SuggestedCypher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Suggest_BuildsRelatedQuery_WhenPromptContainsEntity()
    {
        var service = new QueryAssistantService();

        var response = service.Suggest("show all related to carlsberg");

        Assert.Contains("RETURN n, r, m", response.SuggestedCypher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("carlsberg", response.SuggestedCypher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE n.name = 'carlsberg'", response.SuggestedCypher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Suggest_UsesPromptLimit_WhenProvided()
    {
        var service = new QueryAssistantService();

        var response = service.Suggest("show all limit by 25");

        Assert.Contains("LIMIT 25", response.SuggestedCypher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseLlmResponse_ParsesStrictJson()
    {
        var parsed = QueryAssistantService.TryParseLlmResponse(
            "{\"suggestedCypher\":\"MATCH (n) RETURN n LIMIT 10\",\"explanation\":\"Shows nodes.\"}",
            out var response);

        Assert.True(parsed);
        Assert.Equal("MATCH (n) RETURN n LIMIT 10", response.SuggestedCypher);
        Assert.Equal("Shows nodes.", response.Explanation);
    }

    [Fact]
    public void TryParseLlmResponse_ParsesFencedJson()
    {
        var parsed = QueryAssistantService.TryParseLlmResponse(
            "```json\n{\"suggestedCypher\":\"MATCH (n)-[r]-(m) RETURN n, r, m LIMIT 50\",\"explanation\":\"Shows relationships.\"}\n```",
            out var response);

        Assert.True(parsed);
        Assert.Contains("RETURN n, r, m", response.SuggestedCypher, StringComparison.OrdinalIgnoreCase);
    }
}
