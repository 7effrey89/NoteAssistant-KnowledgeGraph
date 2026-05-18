using System.Text;
using System.Text.RegularExpressions;
using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public sealed class MarkdownGraphIngestionService : IMarkdownGraphIngestionService
{
    private const int MaxChunkCharacters = 280;
    private static int _documentSeed = CreateInitialDocumentSeed();

    public GraphIngestionPlan CreateGraphPlan(string fileName, string markdownContent)
    {
        var documentId = Interlocked.Increment(ref _documentSeed);
        var title = Path.GetFileNameWithoutExtension(fileName);
        var chunks = ChunkMarkdown(markdownContent, documentId).ToList();
        var entities = ExtractEntities(markdownContent, chunks).ToList();
        var mentions = BuildMentions(chunks, entities).ToList();

        var sql = BuildSql(documentId, fileName, title, chunks, entities, mentions);
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
            yield return new ChunkDto(documentId * 1000 + 1, 1, CleanText(normalized));
            yield break;
        }

        var chunkIndex = 1;
        foreach (var block in blocks)
        {
            if (block.Length <= MaxChunkCharacters)
            {
                yield return new ChunkDto(documentId * 1000 + chunkIndex, chunkIndex++, block);
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

                if (current.Length > 0 && current.Length + candidate.Length + 1 > MaxChunkCharacters)
                {
                    yield return new ChunkDto(documentId * 1000 + chunkIndex, chunkIndex++, current.ToString());
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
                yield return new ChunkDto(documentId * 1000 + chunkIndex, chunkIndex++, current.ToString());
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

    private static List<string> BuildSql(int documentId, string fileName, string title, IReadOnlyList<ChunkDto> chunks, IReadOnlyList<EntityDto> entities, IReadOnlyList<ChunkEntityLinkDto> mentions)
    {
        var statements = new List<string>
        {
            "LOAD 'age';",
            "SET search_path = ag_catalog, \"$user\", public;",
            "DO $$ BEGIN CREATE EXTENSION IF NOT EXISTS vector; EXCEPTION WHEN OTHERS THEN RAISE NOTICE 'vector extension not available in this environment.'; END $$;",
            "DO $$ BEGIN CREATE EXTENSION IF NOT EXISTS pg_diskann; EXCEPTION WHEN OTHERS THEN RAISE NOTICE 'pg_diskann extension not available in this environment.'; END $$;",
            "SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM ag_catalog.ag_graph WHERE name = 'knowledge_graph') THEN create_graph('knowledge_graph') END;",
            "CREATE TABLE IF NOT EXISTS documents (id BIGINT PRIMARY KEY, title TEXT NOT NULL, file_name TEXT NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT NOW());",
            "CREATE TABLE IF NOT EXISTS entities (id BIGSERIAL PRIMARY KEY, label TEXT NOT NULL, name TEXT NOT NULL UNIQUE, created_at TIMESTAMPTZ NOT NULL DEFAULT NOW());",
            "CREATE TABLE IF NOT EXISTS chunks (id BIGSERIAL PRIMARY KEY, document_id BIGINT NOT NULL REFERENCES documents(id) ON DELETE CASCADE, chunk_index INTEGER NOT NULL, content TEXT NOT NULL, embedding vector(1536), created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), UNIQUE(document_id, chunk_index));",
            "CREATE TABLE IF NOT EXISTS chunk_entities (chunk_id BIGINT NOT NULL REFERENCES chunks(id) ON DELETE CASCADE, entity_id BIGINT NOT NULL REFERENCES entities(id) ON DELETE CASCADE, PRIMARY KEY(chunk_id, entity_id));",
            "CREATE INDEX IF NOT EXISTS idx_chunks_document ON chunks(document_id, chunk_index);",
            "CREATE INDEX IF NOT EXISTS idx_entities_name ON entities(name);",
            "DO $$ BEGIN CREATE INDEX IF NOT EXISTS idx_chunks_embedding ON chunks USING diskann (embedding vector_cosine_ops); EXCEPTION WHEN OTHERS THEN RAISE NOTICE 'diskann index creation skipped.'; END $$;",
            "CREATE INDEX IF NOT EXISTS idx_chunk_entities_entity_id ON chunk_entities(entity_id);",
            $"INSERT INTO documents(id, title, file_name) VALUES ({documentId}, '{EscapeSql(title)}', '{EscapeSql(fileName)}') ON CONFLICT (id) DO UPDATE SET title = EXCLUDED.title, file_name = EXCLUDED.file_name;"
        };

        foreach (var chunk in chunks)
        {
            statements.Add($"INSERT INTO chunks(document_id, chunk_index, content, embedding) VALUES ({documentId}, {chunk.ChunkIndex}, '{EscapeSql(chunk.Text)}', NULL) ON CONFLICT (document_id, chunk_index) DO UPDATE SET content = EXCLUDED.content;");
        }

        foreach (var entity in entities)
        {
            statements.Add($"INSERT INTO entities(label, name) VALUES ('{EscapeSql(entity.Label)}', '{EscapeSql(entity.Name)}') ON CONFLICT (name) DO UPDATE SET label = EXCLUDED.label;");
            statements.Add($"SELECT * FROM cypher('knowledge_graph', $$ MERGE (e:{entity.Label} {{name:\"{Escape(entity.Name)}\"}}) $$) as (e agtype);");
        }

        var chunkIndexLookup = chunks.ToDictionary(c => c.Id, c => c.ChunkIndex);
        foreach (var mention in mentions)
        {
            if (!chunkIndexLookup.TryGetValue(mention.ChunkId, out var chunkIndex))
            {
                continue;
            }

            statements.Add($"INSERT INTO chunk_entities(chunk_id, entity_id) SELECT c.id, e.id FROM chunks c JOIN entities e ON e.name = '{EscapeSql(mention.EntityName)}' WHERE c.document_id = {documentId} AND c.chunk_index = {chunkIndex} ON CONFLICT DO NOTHING;");
        }

        foreach (var pair in BuildEntityPairsByChunk(mentions))
        {
            statements.Add($"SELECT * FROM cypher('knowledge_graph', $$ MATCH (a {{name:\"{Escape(pair.Left)}\"}}), (b {{name:\"{Escape(pair.Right)}\"}}) MERGE (a)-[:RELATED_TO]->(b) $$) as (v agtype);");
        }

        return statements;
    }

    private static IEnumerable<(string Left, string Right)> BuildEntityPairsByChunk(IEnumerable<ChunkEntityLinkDto> mentions)
    {
        var grouped = mentions
            .GroupBy(m => m.ChunkId)
            .Select(g => g.Select(x => x.EntityName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());

        foreach (var entities in grouped)
        {
            for (var i = 0; i < entities.Count; i++)
            {
                for (var j = i + 1; j < entities.Count; j++)
                {
                    yield return (entities[i], entities[j]);
                }
            }
        }
    }

    private static string Escape(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    private static string EscapeSql(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

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

    private static int CreateInitialDocumentSeed()
        => (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1_000_000);
}
