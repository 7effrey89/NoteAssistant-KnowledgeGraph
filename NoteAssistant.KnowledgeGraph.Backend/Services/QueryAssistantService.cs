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
                "SELECT * FROM cypher('knowledge_graph', $$ MATCH (d:Document)-[:HAS_CHUNK]->(c:Chunk) RETURN d.title, c.id, c.text ORDER BY c.id $$) AS (title agtype, chunk_id agtype, chunk_text agtype);",
                "Returns documents with their chunk sequence.");
        }

        if (value.Contains("entity", StringComparison.OrdinalIgnoreCase) || value.Contains("mentions", StringComparison.OrdinalIgnoreCase))
        {
            return new QueryAssistantResponse(
                "SELECT * FROM cypher('knowledge_graph', $$ MATCH (c:Chunk)-[:MENTIONS]->(e) RETURN c.id, labels(e), e.name ORDER BY c.id $$) AS (chunk_id agtype, labels agtype, entity_name agtype);",
                "Shows which entities are mentioned by each chunk.");
        }

        return new QueryAssistantResponse(
            "SELECT * FROM cypher('knowledge_graph', $$ MATCH p=(n)-[r]->(m) RETURN n, r, m LIMIT 50 $$) AS (n agtype, r agtype, m agtype);",
            "General-purpose graph exploration query that powers the visual explorer.");
    }
}
