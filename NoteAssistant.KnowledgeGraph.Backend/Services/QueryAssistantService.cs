using System.Text.RegularExpressions;
using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public sealed class QueryAssistantService
{
    public QueryAssistantResponse Suggest(string prompt)
    {
        var value = prompt.Trim();
        var limit = TryExtractLimit(value);

        var related = TryBuildRelatedQuery(value, limit);
        if (related is not null)
        {
            return related;
        }

        if (value.Contains("document", StringComparison.OrdinalIgnoreCase) && value.Contains("chunk", StringComparison.OrdinalIgnoreCase))
        {
            return new QueryAssistantResponse(
                $"MATCH (d:Document)-[r:HAS_CHUNK]->(c:Chunk)\nRETURN d, r, c\nORDER BY c.id\nLIMIT {limit ?? 50}",
                "Returns documents with their chunks and the connecting relationships for graph exploration.");
        }

        if (value.Contains("entity", StringComparison.OrdinalIgnoreCase) || value.Contains("mentions", StringComparison.OrdinalIgnoreCase))
        {
            return new QueryAssistantResponse(
                $"MATCH (c:Chunk)\nOPTIONAL MATCH (c)-[r:MENTIONS]->(e)\nRETURN c, r, e\nORDER BY c.id\nLIMIT {limit ?? 50}",
                "Shows chunks and any mentioned entities; still returns chunk nodes even when no mentions exist.");
        }

        return new QueryAssistantResponse(
            $"MATCH p=(n)-[r]->(m)\nRETURN n, r, m\nLIMIT {limit ?? 50}",
            "General-purpose graph exploration query that powers the visual explorer.");
    }

    private static QueryAssistantResponse? TryBuildRelatedQuery(string prompt, int? limit)
    {
        var match = Regex.Match(prompt, @"\brelated to\s+(.+)$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(prompt, @"\babout\s+(.+)$", RegexOptions.IgnoreCase);
        }

        if (!match.Success)
        {
            return null;
        }

        var entity = match.Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(entity))
        {
            return null;
        }

        var safe = EscapeCypherLiteral(entity);
        return new QueryAssistantResponse(
            $"MATCH (n)-[r]-(m)\nWHERE n.name = '{safe}'\nRETURN n, r, m\nLIMIT {limit ?? 50}",
            $"Shows nodes related to '{entity}' and their immediate relationships.");
    }

    private static int? TryExtractLimit(string prompt)
    {
        var match = Regex.Match(prompt, @"\blimit(?:\s+by|\s+to)?\s+(\d{1,4})\b", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(prompt, @"\btop\s+(\d{1,4})\b", RegexOptions.IgnoreCase);
        }

        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var limit))
        {
            return null;
        }

        return Math.Clamp(limit, 1, 200);
    }

    private static string EscapeCypherLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}
