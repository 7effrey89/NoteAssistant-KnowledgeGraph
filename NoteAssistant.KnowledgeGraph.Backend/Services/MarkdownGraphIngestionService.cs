using System.Text;
using System.Text.RegularExpressions;
using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public sealed class MarkdownGraphIngestionService : IMarkdownGraphIngestionService
{
    private const int MaxChunkSize = 280;
    private static int _documentSeed;

    public GraphIngestionPlan CreateGraphPlan(string fileName, string markdownContent)
    {
        var documentId = Interlocked.Increment(ref _documentSeed);
        var title = Path.GetFileNameWithoutExtension(fileName);
        var chunks = ChunkMarkdown(markdownContent, documentId).ToList();
        var entities = ExtractEntities(markdownContent, chunks).ToList();
        var mentions = BuildMentions(chunks, entities).ToList();

        var sql = BuildSql(documentId, title, chunks, entities, mentions);
        var status = new IngestionStatusDto(documentId, fileName, "Analyzed", DateTimeOffset.UtcNow, "Document decomposed into graph elements.");

        return new GraphIngestionPlan(documentId, "knowledge_graph", title, chunks, entities, mentions, sql, status);
    }

    private static IEnumerable<ChunkDto> ChunkMarkdown(string content, int documentId)
    {
        var normalized = (content ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        var blocks = normalized
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanText)
            .Where(block => !string.IsNullOrWhiteSpace(block))
            .ToList();

        if (blocks.Count == 0)
        {
            yield return new ChunkDto(documentId * 1000 + 1, CleanText(normalized));
            yield break;
        }

        var chunkIndex = 1;
        foreach (var block in blocks)
        {
            if (block.Length <= MaxChunkSize)
            {
                yield return new ChunkDto(documentId * 1000 + chunkIndex++, block);
                continue;
            }

            var sentences = Regex.Split(block, @"(?<=[.!?])\s+");
            var current = new StringBuilder();

            foreach (var sentence in sentences)
            {
                var candidate = sentence.Trim();
                if (candidate.Length == 0)
                {
                    continue;
                }

                if (current.Length > 0 && current.Length + candidate.Length + 1 > MaxChunkSize)
                {
                    yield return new ChunkDto(documentId * 1000 + chunkIndex++, current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                }
                current.Append(candidate);
            }

            if (current.Length > 0)
            {
                yield return new ChunkDto(documentId * 1000 + chunkIndex++, current.ToString());
            }
        }
    }

    private static IEnumerable<EntityDto> ExtractEntities(string markdownContent, IReadOnlyList<ChunkDto> chunks)
    {
        var content = markdownContent ?? string.Empty;
        var entitySet = new Dictionary<string, EntityDto>(StringComparer.OrdinalIgnoreCase);

        AddIfMatch("Company", "Microsoft", content, entitySet);
        AddIfMatch("Company", "OpenAI", content, entitySet);
        AddIfMatch("Platform", "Azure", content, entitySet);
        AddIfMatch("Platform", "PostgreSQL", content, entitySet);
        AddIfMatch("Technology", "Apache AGE", content, entitySet);

        foreach (var chunk in chunks)
        {
            foreach (Match match in Regex.Matches(chunk.Text, "\\b[A-Z][A-Za-z]+(?:\\s+[A-Z][A-Za-z]+)?\\b"))
            {
                var name = match.Value.Trim();
                if (name.Length < 3 || name.Length > 40)
                {
                    continue;
                }

                var label = name.Contains("AI", StringComparison.OrdinalIgnoreCase) ? "Topic" : "Concept";
                entitySet.TryAdd($"{label}:{name}", new EntityDto(label, name));
            }

            var keyword = ExtractKeyword(chunk.Text);
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                entitySet.TryAdd($"Topic:{keyword}", new EntityDto("Topic", keyword));
            }
        }

        return entitySet.Values
            .OrderBy(e => e.Label, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<ChunkEntityLinkDto> BuildMentions(IReadOnlyList<ChunkDto> chunks, IReadOnlyList<EntityDto> entities)
    {
        foreach (var chunk in chunks)
        {
            foreach (var entity in entities)
            {
                if (chunk.Text.Contains(entity.Name, StringComparison.OrdinalIgnoreCase))
                {
                    yield return new ChunkEntityLinkDto(chunk.Id, entity.Label, entity.Name);
                }
            }
        }
    }

    private static List<string> BuildSql(int documentId, string title, IReadOnlyList<ChunkDto> chunks, IReadOnlyList<EntityDto> entities, IReadOnlyList<ChunkEntityLinkDto> mentions)
    {
        var statements = new List<string>
        {
            "LOAD 'age';",
            "SET search_path = ag_catalog, \"$user\", public;",
            "SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM ag_catalog.ag_graph WHERE name = 'knowledge_graph') THEN create_graph('knowledge_graph') END;",
            $"SELECT * FROM cypher('knowledge_graph', $$ CREATE (d:Document {{id: {documentId}, title: \"{Escape(title)}\"}}) $$) as (d agtype);"
        };

        foreach (var chunk in chunks)
        {
            statements.Add($"SELECT * FROM cypher('knowledge_graph', $$ CREATE (c:Chunk {{id: {chunk.Id}, text: \"{Escape(chunk.Text)}\"}}) $$) as (c agtype);");
        }

        for (var i = 0; i < chunks.Count - 1; i++)
        {
            statements.Add($"SELECT * FROM cypher('knowledge_graph', $$ MATCH (c1:Chunk {{id:{chunks[i].Id}}}), (c2:Chunk {{id:{chunks[i + 1].Id}}}) CREATE (c1)-[:NEXT]->(c2) $$) as (v agtype);");
        }

        foreach (var entity in entities)
        {
            statements.Add($"SELECT * FROM cypher('knowledge_graph', $$ MERGE (e:{entity.Label} {{name:\"{Escape(entity.Name)}\"}}) $$) as (e agtype);");
        }

        foreach (var mention in mentions)
        {
            statements.Add($"SELECT * FROM cypher('knowledge_graph', $$ MATCH (c:Chunk {{id:{mention.ChunkId}}}), (e:{mention.EntityLabel} {{name:\"{Escape(mention.EntityName)}\"}}) MERGE (c)-[:MENTIONS]->(e) $$) as (v agtype);");
        }

        statements.Add($"SELECT * FROM cypher('knowledge_graph', $$ MATCH (d:Document {{id:{documentId}}}), (c:Chunk) WHERE c.id >= {documentId * 1000} AND c.id < {(documentId + 1) * 1000} MERGE (d)-[:HAS_CHUNK]->(c) $$) as (v agtype);");

        statements.Add("SELECT * FROM cypher('knowledge_graph', $$ MATCH (m:Company {name:\"Microsoft\"}), (o:Company {name:\"OpenAI\"}) MERGE (m)-[:PARTNERED_WITH]->(o) $$) as (v agtype);");
        statements.Add("SELECT * FROM cypher('knowledge_graph', $$ MATCH (m:Company {name:\"Microsoft\"}), (t:Topic {name:\"AI infrastructure\"}) MERGE (m)-[:INVESTS_IN]->(t) $$) as (v agtype);");
        statements.Add("SELECT * FROM cypher('knowledge_graph', $$ MATCH (m:Company {name:\"Microsoft\"}), (p:Platform {name:\"Azure\"}) MERGE (m)-[:EXPANDS]->(p) $$) as (v agtype);");

        return statements;
    }

    private static string Escape(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    private static string CleanText(string value)
    {
        var withoutHeaders = Regex.Replace(value, "^#{1,6}\\s*", string.Empty, RegexOptions.Multiline);
        return Regex.Replace(withoutHeaders, "\\s+", " ").Trim();
    }

    private static void AddIfMatch(string label, string name, string content, IDictionary<string, EntityDto> entities)
    {
        if (content.Contains(name, StringComparison.OrdinalIgnoreCase))
        {
            entities.TryAdd($"{label}:{name}", new EntityDto(label, name));
        }
    }

    private static string ExtractKeyword(string text)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "with", "that", "this", "from", "into", "have", "will", "your", "about", "for", "are", "has"
        };

        var candidate = Regex.Matches(text, "\\b[a-zA-Z]{4,}\\b")
            .Select(m => m.Value)
            .FirstOrDefault(word => !stopWords.Contains(word));

        return candidate is null ? string.Empty : candidate.ToLowerInvariant();
    }
}
