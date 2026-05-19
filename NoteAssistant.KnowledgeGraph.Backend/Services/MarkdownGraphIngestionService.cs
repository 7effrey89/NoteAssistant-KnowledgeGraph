using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public sealed class MarkdownGraphIngestionService : IMarkdownGraphIngestionService
{
    private const int MaxChunkCharacters = 280;
    private static int _documentSeed = CreateInitialDocumentSeed();
    private readonly IFoundryInferenceClient _foundry;
    private readonly DatabaseOptions _databaseOptions;

    public MarkdownGraphIngestionService(IFoundryInferenceClient foundry, IOptions<DatabaseOptions> databaseOptions)
    {
        _foundry = foundry;
        _databaseOptions = databaseOptions.Value;
    }

    public async Task<GraphIngestionPlan> CreateGraphPlanAsync(string fileName, string markdownContent, CancellationToken cancellationToken)
    {
        if (!_foundry.IsConfigured)
        {
            throw new InvalidOperationException("Foundry inference is not configured. Set Copilot settings and credentials.");
        }

        var documentId = Interlocked.Increment(ref _documentSeed);
        var title = Path.GetFileNameWithoutExtension(fileName);
        var chunks = ChunkMarkdown(markdownContent, documentId).ToList();
        var chunkEmbeddings = await _foundry.CreateEmbeddingsAsync(chunks.Select(c => c.Text).ToList(), cancellationToken);
        var enrichedChunks = MergeEmbeddings(chunks, chunkEmbeddings);

        var entities = await ExtractEntitiesAsync(markdownContent, enrichedChunks, cancellationToken);
        var mentions = BuildMentions(enrichedChunks, entities).ToList();

        var sql = BuildSql(documentId, fileName, title, enrichedChunks, entities, mentions);
        var status = new IngestionStatusDto(documentId, fileName, "Analyzed", DateTimeOffset.UtcNow, "Document decomposed into graph elements.");

        return new GraphIngestionPlan(documentId, _databaseOptions.GraphName, title, enrichedChunks, entities, mentions, sql, status, markdownContent ?? string.Empty);
    }

    public GraphIngestionPlan RefreshSql(GraphIngestionPlan plan)
    {
        var fileName = plan.Status.FileName;
        var sql = BuildSql(plan.DocumentId, fileName, plan.Title, plan.Chunks, plan.Entities, plan.Mentions);
        return plan with { GraphName = _databaseOptions.GraphName, SqlStatements = sql };
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

    private async Task<IReadOnlyList<EntityDto>> ExtractEntitiesAsync(string markdownContent, IReadOnlyList<ChunkDto> chunks, CancellationToken cancellationToken)
    {
        var extracted = await _foundry.ExtractEntitiesAsync(markdownContent, cancellationToken);
        var normalized = NormalizeEntities(extracted);

        if (normalized.Count > 0)
        {
            return normalized;
        }

        return ExtractEntitiesHeuristic(markdownContent, chunks).ToList();
    }

    private static IEnumerable<EntityDto> ExtractEntitiesHeuristic(string markdownContent, IReadOnlyList<ChunkDto> chunks)
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

    private static IReadOnlyList<EntityDto> NormalizeEntities(IEnumerable<EntityDto> entities)
    {
        var map = new Dictionary<string, EntityDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities)
        {
            var name = entity.Name?.Trim();
            var label = entity.Label?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (name.Length is < 2 or > 80)
            {
                continue;
            }

            label = string.IsNullOrWhiteSpace(label) ? "Concept" : label;
            map.TryAdd($"{label}:{name}", new EntityDto(label, name));
        }

        return map.Values
            .OrderBy(e => e.Label, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private List<string> BuildSql(int documentId, string fileName, string title, IReadOnlyList<ChunkDto> chunks, IReadOnlyList<EntityDto> entities, IReadOnlyList<ChunkEntityLinkDto> mentions)
    {
        var graphName = _databaseOptions.GraphName;
        var extensions = _databaseOptions.Extensions;
        var schema = NormalizeSchemaName(_databaseOptions.SchemaName);
        var statements = new List<string>
        {
            BuildExtensionStatement(extensions, "age"),
            BuildExtensionStatement(extensions, "vector"),
            BuildExtensionStatement(extensions, "pg_diskann"),
            $"CREATE SCHEMA IF NOT EXISTS {schema};",
            $"SET search_path = {schema}, public, ag_catalog;",
            "CREATE TABLE IF NOT EXISTS documents (id BIGINT PRIMARY KEY, title TEXT NOT NULL, file_name TEXT NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT NOW());",
            "CREATE TABLE IF NOT EXISTS entities (id BIGSERIAL PRIMARY KEY, label TEXT NOT NULL, name TEXT NOT NULL UNIQUE, created_at TIMESTAMPTZ NOT NULL DEFAULT NOW());",
            "CREATE TABLE IF NOT EXISTS chunks (id BIGSERIAL PRIMARY KEY, document_id BIGINT NOT NULL REFERENCES documents(id) ON DELETE CASCADE, chunk_index INTEGER NOT NULL, content TEXT NOT NULL, embedding vector(1536), created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), UNIQUE(document_id, chunk_index));",
            "CREATE TABLE IF NOT EXISTS chunk_entities (chunk_id BIGINT NOT NULL REFERENCES chunks(id) ON DELETE CASCADE, entity_id BIGINT NOT NULL REFERENCES entities(id) ON DELETE CASCADE, PRIMARY KEY(chunk_id, entity_id));",
            "CREATE INDEX IF NOT EXISTS idx_chunks_document ON chunks(document_id, chunk_index);",
            "CREATE INDEX IF NOT EXISTS idx_entities_name ON entities(name);",
            BuildDiskAnnIndexStatement(extensions),
            "CREATE INDEX IF NOT EXISTS idx_chunk_entities_entity_id ON chunk_entities(entity_id);",
            $"INSERT INTO documents(id, title, file_name) VALUES ({documentId}, '{EscapeSql(title)}', '{EscapeSql(fileName)}') ON CONFLICT (id) DO UPDATE SET title = EXCLUDED.title, file_name = EXCLUDED.file_name;",
            $"SELECT * FROM ag_catalog.cypher('{EscapeSql(graphName)}', $$ MERGE (d:Document {{id:{documentId}}}) SET d.title=\"{Escape(title)}\", d.file_name=\"{Escape(fileName)}\" RETURN d $$) as (d agtype);"
        };

        foreach (var chunk in chunks)
        {
            var embeddingLiteral = chunk.Embedding is { Length: > 0 }
                ? $"'{ToVectorLiteral(chunk.Embedding, 1536)}'"
                : "NULL";
            statements.Add($"INSERT INTO chunks(document_id, chunk_index, content, embedding) VALUES ({documentId}, {chunk.ChunkIndex}, '{EscapeSql(chunk.Text)}', {embeddingLiteral}) ON CONFLICT (document_id, chunk_index) DO UPDATE SET content = EXCLUDED.content, embedding = EXCLUDED.embedding;");
            statements.Add($"SELECT * FROM ag_catalog.cypher('{EscapeSql(graphName)}', $$ MERGE (c:Chunk {{id:{chunk.Id}}}) SET c.document_id={documentId}, c.chunk_index={chunk.ChunkIndex}, c.text=\"{Escape(chunk.Text)}\" RETURN c $$) as (c agtype);");
            statements.Add($"SELECT * FROM ag_catalog.cypher('{EscapeSql(graphName)}', $$ MATCH (d:Document {{id:{documentId}}}), (c:Chunk {{id:{chunk.Id}}}) MERGE (d)-[r:HAS_CHUNK]->(c) RETURN r $$) as (r agtype);");
        }

        foreach (var entity in entities)
        {
            statements.Add($"INSERT INTO entities(label, name) VALUES ('{EscapeSql(entity.Label)}', '{EscapeSql(entity.Name)}') ON CONFLICT (name) DO UPDATE SET label = EXCLUDED.label;");
            statements.Add($"SELECT * FROM ag_catalog.cypher('{EscapeSql(graphName)}', $$ MERGE (e:{entity.Label} {{name:\"{Escape(entity.Name)}\"}}) RETURN e $$) as (e agtype);");
        }

        var chunkIndexLookup = chunks.ToDictionary(c => c.Id, c => c.ChunkIndex);
        foreach (var mention in mentions)
        {
            if (!chunkIndexLookup.TryGetValue(mention.ChunkId, out var chunkIndex))
            {
                continue;
            }

            statements.Add($"INSERT INTO chunk_entities(chunk_id, entity_id) SELECT c.id, e.id FROM chunks c JOIN entities e ON e.name = '{EscapeSql(mention.EntityName)}' WHERE c.document_id = {documentId} AND c.chunk_index = {chunkIndex} ON CONFLICT DO NOTHING;");
            statements.Add($"SELECT * FROM ag_catalog.cypher('{EscapeSql(graphName)}', $$ MATCH (c:Chunk {{id:{mention.ChunkId}}}), (e) WHERE e.name=\"{Escape(mention.EntityName)}\" MERGE (c)-[r:MENTIONS]->(e) RETURN r $$) as (r agtype);");
        }

        foreach (var pair in BuildEntityPairsByChunk(mentions))
        {
            statements.Add($"SELECT * FROM ag_catalog.cypher('{EscapeSql(graphName)}', $$ MATCH (a), (b) WHERE a.name=\"{Escape(pair.Left)}\" AND b.name=\"{Escape(pair.Right)}\" MERGE (a)-[r:RELATED_TO]->(b) RETURN r $$) as (r agtype);");
        }

        return statements;
    }

    private static string NormalizeSchemaName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "kg_data";
        }

        var trimmed = value.Trim();
        foreach (var ch in trimmed)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_')
            {
                return "kg_data";
            }
        }

        return trimmed;
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

    private static string BuildExtensionStatement(string[] extensions, string extensionName)
        => extensions.Any(name => string.Equals(name, extensionName, StringComparison.OrdinalIgnoreCase))
            ? $"DO $$ BEGIN CREATE EXTENSION IF NOT EXISTS {extensionName}; EXCEPTION WHEN OTHERS THEN RAISE NOTICE '{extensionName} extension not available in this environment.'; END $$;"
            : $"DO $$ BEGIN RAISE NOTICE '{extensionName} extension disabled by configuration.'; END $$;";

    private static string BuildDiskAnnIndexStatement(string[] extensions)
        => extensions.Any(name => string.Equals(name, "pg_diskann", StringComparison.OrdinalIgnoreCase))
            ? "DO $$ BEGIN CREATE INDEX IF NOT EXISTS idx_chunks_embedding ON chunks USING diskann (embedding vector_cosine_ops); EXCEPTION WHEN OTHERS THEN RAISE NOTICE 'diskann index creation skipped.'; END $$;"
            : "DO $$ BEGIN RAISE NOTICE 'diskann index creation disabled by configuration.'; END $$;";

    private static IReadOnlyList<ChunkDto> MergeEmbeddings(IReadOnlyList<ChunkDto> chunks, IReadOnlyList<float[]> embeddings)
    {
        if (chunks.Count != embeddings.Count)
        {
            throw new InvalidOperationException("Embedding count does not match chunk count.");
        }

        var enriched = new List<ChunkDto>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            enriched.Add(chunk with { Embedding = embeddings[i] });
        }

        return enriched;
    }

    private static string ToVectorLiteral(float[] input, int dimension)
    {
        var vector = new float[dimension];
        var length = Math.Min(input.Length, dimension);
        Array.Copy(input, vector, length);
        return $"[{string.Join(",", vector.Select(v => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)))}]";
    }
}
