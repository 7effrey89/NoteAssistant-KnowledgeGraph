using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public sealed class QueryAssistantService
{
    public QueryAssistantResponse Suggest(string prompt)
    {
        var value = prompt.Trim();

        if (value.Contains("document", StringComparison.OrdinalIgnoreCase) && value.Contains("chunk", StringComparison.OrdinalIgnoreCase))
        {
            return new QueryAssistantResponse(
                "MATCH (d:Document)-[:HAS_CHUNK]->(c:Chunk)\nRETURN d.title, c.id, c.text\nORDER BY c.id",
                "Returns documents with their chunk sequence.");
        }

        if (value.Contains("entity", StringComparison.OrdinalIgnoreCase) || value.Contains("mentions", StringComparison.OrdinalIgnoreCase))
        {
            return new QueryAssistantResponse(
                "MATCH (c:Chunk)-[:MENTIONS]->(e)\nRETURN c.id, labels(e), e.name\nORDER BY c.id",
                "Shows which entities are mentioned by each chunk.");
        }

        return new QueryAssistantResponse(
            "MATCH p=(n)-[r]->(m)\nRETURN n, r, m\nLIMIT 50",
            "General-purpose graph exploration query that powers the visual explorer.");
    }
}
