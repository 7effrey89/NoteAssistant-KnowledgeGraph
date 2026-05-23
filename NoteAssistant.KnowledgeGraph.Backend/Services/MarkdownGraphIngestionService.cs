using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.Extensions.Options;
using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public sealed class MarkdownGraphIngestionService : IMarkdownGraphIngestionService
{
    private const int MaxChunkCharacters = 280;
    private static long _documentSeed = CreateInitialDocumentSeed();
    private readonly IFoundryInferenceClient _foundry;
    private readonly DatabaseOptions _databaseOptions;

    public MarkdownGraphIngestionService(IFoundryInferenceClient foundry, IOptions<DatabaseOptions> databaseOptions)
    {
        _foundry = foundry;
        _databaseOptions = databaseOptions.Value;
    }

    public async Task<GraphIngestionPlan> CreateGraphPlanAsync(string fileName, string markdownContent, DocumentMetadata? metadata, string contentHash, CancellationToken cancellationToken)
    {
        if (!_foundry.IsConfigured)
        {
            throw new InvalidOperationException("Foundry inference is not configured. Set Copilot settings and credentials.");
        }

        var documentId = CreateDocumentId(contentHash);
        var title = Path.GetFileNameWithoutExtension(fileName);
        var chunks = ChunkMarkdown(markdownContent, documentId).ToList();
        var chunkEmbeddings = await _foundry.CreateEmbeddingsAsync(chunks.Select(c => c.Text).ToList(), cancellationToken);
        var enrichedChunks = MergeEmbeddings(chunks, chunkEmbeddings);

        var extraction = await ExtractGraphAsync(markdownContent, enrichedChunks, cancellationToken);
        var entities = extraction.Entities;
        var mentions = BuildMentions(enrichedChunks, entities).ToList();
        var relationships = BuildChunkRelationships(enrichedChunks, extraction.Relationships).ToList();
        entities = await EnrichEntityEmbeddingsAsync(entities, mentions, relationships, enrichedChunks, cancellationToken);

        var normalizedMetadata = NormalizeMetadata(metadata);
        var sql = BuildSql(documentId, fileName, title, markdownContent ?? string.Empty, contentHash, enrichedChunks, entities, mentions, relationships, normalizedMetadata);
        var status = new IngestionStatusDto(documentId, fileName, "Analyzed", DateTimeOffset.UtcNow, "Document decomposed into graph elements.");

        return new GraphIngestionPlan(documentId, _databaseOptions.GraphName, title, normalizedMetadata, enrichedChunks, entities, mentions, sql, status, markdownContent ?? string.Empty, contentHash, DecompositionSystemPrompt: _foundry.EntityExtractionSystemPrompt, Relationships: relationships);
    }

    public GraphIngestionPlan ApplyDocumentIdentity(GraphIngestionPlan plan, string contentHash)
    {
        var documentId = CreateDocumentId(contentHash);
        var chunkIdMap = plan.Chunks.ToDictionary(chunk => chunk.Id, chunk => BuildChunkId(documentId, chunk.ChunkIndex));
        var chunks = plan.Chunks
            .Select(chunk => chunk with { Id = chunkIdMap[chunk.Id] })
            .ToList();
        var mentions = plan.Mentions
            .Select(mention => chunkIdMap.TryGetValue(mention.ChunkId, out var chunkId)
                ? mention with { ChunkId = chunkId }
                : mention)
            .ToList();
        var relationships = (plan.Relationships ?? Array.Empty<ChunkRelationshipDto>())
            .Select(relationship => relationship.ChunkId.HasValue && chunkIdMap.TryGetValue(relationship.ChunkId.Value, out var chunkId)
                ? relationship with { ChunkId = chunkId }
                : relationship)
            .ToList();
        var status = plan.Status with { DocumentId = documentId };

        return plan with
        {
            DocumentId = documentId,
            Chunks = chunks,
            Mentions = mentions,
            Status = status,
            ContentHash = contentHash,
            Relationships = relationships
        };
    }

    public GraphIngestionPlan RefreshSql(GraphIngestionPlan plan)
    {
        var fileName = plan.Status.FileName;
        var sql = BuildSql(plan.DocumentId, fileName, plan.Title, plan.OriginalContent ?? string.Empty, plan.ContentHash, plan.Chunks, plan.Entities, plan.Mentions, plan.Relationships ?? Array.Empty<ChunkRelationshipDto>(), NormalizeMetadata(plan.Metadata));
        return plan with { GraphName = _databaseOptions.GraphName, SqlStatements = sql, DecompositionSystemPrompt = string.IsNullOrWhiteSpace(plan.DecompositionSystemPrompt) ? _foundry.EntityExtractionSystemPrompt : plan.DecompositionSystemPrompt };
    }

    private static IEnumerable<ChunkDto> ChunkMarkdown(string content, long documentId)
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
            yield return new ChunkDto(BuildChunkId(documentId, 1), 1, CleanText(normalized));
            yield break;
        }

        var chunkIndex = 1;
        foreach (var block in blocks)
        {
            if (block.Length <= MaxChunkCharacters)
            {
                yield return new ChunkDto(BuildChunkId(documentId, chunkIndex), chunkIndex++, block);
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
                    yield return new ChunkDto(BuildChunkId(documentId, chunkIndex), chunkIndex++, current.ToString());
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
                yield return new ChunkDto(BuildChunkId(documentId, chunkIndex), chunkIndex++, current.ToString());
            }
        }
    }

    private async Task<GraphExtractionDto> ExtractGraphAsync(string markdownContent, IReadOnlyList<ChunkDto> chunks, CancellationToken cancellationToken)
    {
        var extracted = await _foundry.ExtractGraphAsync(markdownContent, cancellationToken);
        var normalized = NormalizeEntities(extracted.Entities);
        var relationships = NormalizeRelationships(extracted.Relationships, normalized);

        if (normalized.Count > 0)
        {
            return new GraphExtractionDto(normalized, relationships);
        }

        return new GraphExtractionDto(ExtractEntitiesHeuristic(markdownContent, chunks).ToList(), Array.Empty<RelationshipDto>());
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

    private static IReadOnlyList<RelationshipDto> NormalizeRelationships(IEnumerable<RelationshipDto> relationships, IReadOnlyList<EntityDto> entities)
    {
        var entityNames = entities.Select(entity => entity.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalized = new Dictionary<string, RelationshipDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var relationship in relationships)
        {
            var source = relationship.SourceName?.Trim();
            var target = relationship.TargetName?.Trim();
            var type = NormalizeRelationshipType(relationship.Relationship);
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            if (source.Length > 80 || target.Length > 80 || type.Length > 80)
            {
                continue;
            }

            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entityNames.Count > 0 && (!entityNames.Contains(source) || !entityNames.Contains(target)))
            {
                continue;
            }

            double? confidence = relationship.Confidence.HasValue
                ? Math.Clamp(relationship.Confidence.Value, 0, 1)
                : null;
            normalized.TryAdd($"{source}\t{type}\t{target}", new RelationshipDto(source, type, target, confidence));
        }

        return normalized.Values
            .OrderBy(r => r.SourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Relationship, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.TargetName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<ChunkRelationshipDto> BuildChunkRelationships(IReadOnlyList<ChunkDto> chunks, IReadOnlyList<RelationshipDto> relationships)
    {
        foreach (var relationship in relationships)
        {
            var supportingChunk = chunks.FirstOrDefault(chunk =>
                chunk.Text.Contains(relationship.SourceName, StringComparison.OrdinalIgnoreCase)
                && chunk.Text.Contains(relationship.TargetName, StringComparison.OrdinalIgnoreCase));

            yield return new ChunkRelationshipDto(
                supportingChunk?.Id,
                relationship.SourceName,
                relationship.Relationship,
                relationship.TargetName,
                relationship.Confidence,
                supportingChunk?.Text);
        }
    }

    private async Task<IReadOnlyList<EntityDto>> EnrichEntityEmbeddingsAsync(
        IReadOnlyList<EntityDto> entities,
        IReadOnlyList<ChunkEntityLinkDto> mentions,
        IReadOnlyList<ChunkRelationshipDto> relationships,
        IReadOnlyList<ChunkDto> chunks,
        CancellationToken cancellationToken)
    {
        if (entities.Count == 0)
        {
            return entities;
        }

        var chunkLookup = chunks.ToDictionary(chunk => chunk.Id, chunk => chunk.Text);
        var embeddingTexts = entities
            .Select(entity => BuildEntityEmbeddingText(entity, mentions, relationships, chunkLookup))
            .ToList();
        var embeddings = await _foundry.CreateEmbeddingsAsync(embeddingTexts, cancellationToken);

        return entities.Select((entity, index) => entity with
        {
            EmbeddingText = embeddingTexts[index],
            Embedding = index < embeddings.Count ? embeddings[index] : null
        }).ToList();
    }

    private static string BuildEntityEmbeddingText(
        EntityDto entity,
        IReadOnlyList<ChunkEntityLinkDto> mentions,
        IReadOnlyList<ChunkRelationshipDto> relationships,
        IReadOnlyDictionary<long, string> chunkLookup)
    {
        var lines = new List<string>
        {
            $"Entity: {entity.Name}",
            $"Type: {entity.Label}"
        };

        var relationLines = relationships
            .Where(relationship => string.Equals(relationship.SourceName, entity.Name, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(relationship.TargetName, entity.Name, StringComparison.OrdinalIgnoreCase))
            .Select(relationship => $"Relation: {relationship.SourceName} {relationship.Relationship} {relationship.TargetName}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        lines.AddRange(relationLines);

        var sourceTexts = mentions
            .Where(mention => string.Equals(mention.EntityName, entity.Name, StringComparison.OrdinalIgnoreCase))
            .Select(mention => chunkLookup.TryGetValue(mention.ChunkId, out var text) ? text : null)
            .Concat(relationships
                .Where(relationship => string.Equals(relationship.SourceName, entity.Name, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(relationship.TargetName, entity.Name, StringComparison.OrdinalIgnoreCase))
                .Select(relationship => relationship.Evidence))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        if (sourceTexts.Count > 0)
        {
            lines.Add($"Context: {string.Join(" ", sourceTexts)}");
        }

        return string.Join(Environment.NewLine, lines);
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

    private List<string> BuildSql(
        long documentId,
        string fileName,
        string title,
        string rawContent,
        string contentHash,
        IReadOnlyList<ChunkDto> chunks,
        IReadOnlyList<EntityDto> entities,
        IReadOnlyList<ChunkEntityLinkDto> mentions,
        IReadOnlyList<ChunkRelationshipDto> relationships,
        DocumentMetadata metadata)
    {
        var graphName = _databaseOptions.GraphName;
        var extensions = _databaseOptions.Extensions;
        var schema = NormalizeSchemaName(_databaseOptions.SchemaName);
        var docTypeLiteral = string.IsNullOrWhiteSpace(metadata.DocumentType) ? "NULL" : $"'{EscapeSql(metadata.DocumentType)}'";
        var docDateLiteral = metadata.DocumentDate.HasValue ? $"DATE '{metadata.DocumentDate.Value:yyyy-MM-dd}'" : "NULL";
        var contentLiteral = string.IsNullOrWhiteSpace(rawContent) ? "NULL" : $"'{EscapeSql(rawContent)}'";
        var contentHashLiteral = string.IsNullOrWhiteSpace(contentHash) ? "NULL" : $"'{EscapeSql(contentHash)}'";
        var tagsJsonLiteral = BuildTagsJsonLiteral(metadata.Tags);
        var cypherTagsLiteral = BuildCypherTagsLiteral(metadata.Tags);
        var statements = new List<string>
        {
            BuildExtensionStatement(extensions, "age"),
            BuildExtensionStatement(extensions, "vector"),
            BuildExtensionStatement(extensions, "pg_diskann"),
            $"CREATE SCHEMA IF NOT EXISTS {schema};",
            $"SET search_path = {schema}, public, ag_catalog;",
            "CREATE TABLE IF NOT EXISTS documents (id BIGINT PRIMARY KEY, title TEXT NOT NULL, file_name TEXT NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT NOW());",
            "ALTER TABLE documents ADD COLUMN IF NOT EXISTS document_type TEXT;",
            "ALTER TABLE documents ADD COLUMN IF NOT EXISTS document_date DATE;",
            "ALTER TABLE documents ADD COLUMN IF NOT EXISTS content TEXT;",
            "ALTER TABLE documents ADD COLUMN IF NOT EXISTS content_hash TEXT;",
            "ALTER TABLE documents ADD COLUMN IF NOT EXISTS tags JSONB;",
            "ALTER TABLE documents DROP COLUMN IF EXISTS source_created_at;",
            "CREATE TABLE IF NOT EXISTS entities (id BIGSERIAL PRIMARY KEY, label TEXT NOT NULL, name TEXT NOT NULL UNIQUE, created_at TIMESTAMPTZ NOT NULL DEFAULT NOW());",
            "ALTER TABLE entities ADD COLUMN IF NOT EXISTS embedding vector(1536);",
            "ALTER TABLE entities ADD COLUMN IF NOT EXISTS embedding_text TEXT;",
            "CREATE TABLE IF NOT EXISTS chunks (id BIGSERIAL PRIMARY KEY, document_id BIGINT NOT NULL REFERENCES documents(id) ON DELETE CASCADE, chunk_index INTEGER NOT NULL, content TEXT NOT NULL, embedding vector(1536), created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), UNIQUE(document_id, chunk_index));",
            "CREATE TABLE IF NOT EXISTS chunk_entities (chunk_id BIGINT NOT NULL REFERENCES chunks(id) ON DELETE CASCADE, entity_id BIGINT NOT NULL REFERENCES entities(id) ON DELETE CASCADE, PRIMARY KEY(chunk_id, entity_id));",
            "CREATE TABLE IF NOT EXISTS relationships (id BIGSERIAL PRIMARY KEY, source_entity_id BIGINT NOT NULL REFERENCES entities(id) ON DELETE CASCADE, target_entity_id BIGINT NOT NULL REFERENCES entities(id) ON DELETE CASCADE, relationship_type TEXT NOT NULL, chunk_id BIGINT NULL REFERENCES chunks(id) ON DELETE SET NULL, confidence DOUBLE PRECISION NULL, evidence TEXT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), UNIQUE(source_entity_id, target_entity_id, relationship_type, chunk_id));",
            "CREATE INDEX IF NOT EXISTS idx_chunks_document ON chunks(document_id, chunk_index);",
            "CREATE INDEX IF NOT EXISTS idx_entities_name ON entities(name);",
            BuildEntityDiskAnnIndexStatement(extensions),
            BuildDiskAnnIndexStatement(extensions),
            "CREATE INDEX IF NOT EXISTS idx_chunk_entities_entity_id ON chunk_entities(entity_id);",
            "CREATE INDEX IF NOT EXISTS idx_relationships_source ON relationships(source_entity_id);",
            "CREATE INDEX IF NOT EXISTS idx_relationships_target ON relationships(target_entity_id);",
            "CREATE INDEX IF NOT EXISTS idx_relationships_type ON relationships(relationship_type);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_documents_content_hash ON documents(content_hash) WHERE content_hash IS NOT NULL AND content_hash <> '';",
            "CREATE INDEX IF NOT EXISTS idx_documents_tags ON documents USING GIN (tags);",
            "DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'documents' AND column_name = 'summary') AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'documents' AND column_name = 'content') THEN ALTER TABLE documents RENAME COLUMN summary TO content; END IF; END $$;",
            $"INSERT INTO documents(id, title, file_name, document_type, document_date, content, content_hash, tags) VALUES ({documentId}, '{EscapeSql(title)}', '{EscapeSql(fileName)}', {docTypeLiteral}, {docDateLiteral}, {contentLiteral}, {contentHashLiteral}, {tagsJsonLiteral}) ON CONFLICT (id) DO UPDATE SET title = EXCLUDED.title, file_name = EXCLUDED.file_name, document_type = EXCLUDED.document_type, document_date = EXCLUDED.document_date, content = EXCLUDED.content, content_hash = EXCLUDED.content_hash, tags = EXCLUDED.tags;",
            $"SELECT * FROM ag_catalog.cypher('{EscapeSql(graphName)}', $$ MERGE (d:Document {{id:{documentId}}}) SET d.title=\"{Escape(title)}\", d.file_name=\"{Escape(fileName)}\", d.document_type={(string.IsNullOrWhiteSpace(metadata.DocumentType) ? "null" : $"\"{Escape(metadata.DocumentType)}\"")}, d.document_date={(metadata.DocumentDate.HasValue ? $"\"{metadata.DocumentDate.Value:yyyy-MM-dd}\"" : "null")}, d.content={(string.IsNullOrWhiteSpace(rawContent) ? "null" : $"\"{Escape(rawContent)}\"")}, d.content_hash={(string.IsNullOrWhiteSpace(contentHash) ? "null" : $"\"{Escape(contentHash)}\"")}, d.tags={cypherTagsLiteral} RETURN d $$) as (d agtype);"
        };

        if (metadata.Tags is { Count: > 0 })
        {
            foreach (var tag in metadata.Tags)
            {
                statements.Add($"INSERT INTO entities(label, name) VALUES ('Tag', '{EscapeSql(tag)}') ON CONFLICT (name) DO NOTHING;");
                statements.Add($"SELECT * FROM ag_catalog.cypher('{EscapeSql(graphName)}', $$ MERGE (t:Tag {{name:\"{Escape(tag)}\"}}) RETURN t $$) as (t agtype);");
                statements.Add($"SELECT * FROM ag_catalog.cypher('{EscapeSql(graphName)}', $$ MATCH (d:Document {{id:{documentId}}}), (t:Tag {{name:\"{Escape(tag)}\"}}) MERGE (d)-[r:TAGGED_WITH]->(t) RETURN r $$) as (r agtype);");
            }
        }

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
            var entityEmbeddingLiteral = entity.Embedding is { Length: > 0 }
                ? $"'{ToVectorLiteral(entity.Embedding, 1536)}'"
                : "NULL";
            var entityEmbeddingTextLiteral = string.IsNullOrWhiteSpace(entity.EmbeddingText)
                ? "NULL"
                : $"'{EscapeSql(entity.EmbeddingText)}'";
            statements.Add($"INSERT INTO entities(label, name, embedding, embedding_text) VALUES ('{EscapeSql(entity.Label)}', '{EscapeSql(entity.Name)}', {entityEmbeddingLiteral}, {entityEmbeddingTextLiteral}) ON CONFLICT (name) DO UPDATE SET label = EXCLUDED.label, embedding = COALESCE(EXCLUDED.embedding, entities.embedding), embedding_text = COALESCE(EXCLUDED.embedding_text, entities.embedding_text);");
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

        foreach (var relationship in relationships)
        {
            var relationshipChunkIndex = relationship.ChunkId.HasValue && chunkIndexLookup.TryGetValue(relationship.ChunkId.Value, out var resolvedChunkIndex)
                ? resolvedChunkIndex
                : (int?)null;
            var chunkJoin = relationshipChunkIndex.HasValue
                ? $"LEFT JOIN chunks c ON c.document_id = {documentId} AND c.chunk_index = {relationshipChunkIndex.Value}"
                : "LEFT JOIN chunks c ON false";
            var confidenceLiteral = relationship.Confidence.HasValue ? relationship.Confidence.Value.ToString("0.####", CultureInfo.InvariantCulture) : "NULL";
            var evidenceLiteral = string.IsNullOrWhiteSpace(relationship.Evidence) ? "NULL" : $"'{EscapeSql(relationship.Evidence)}'";
            statements.Add($"INSERT INTO relationships(source_entity_id, target_entity_id, relationship_type, chunk_id, confidence, evidence) SELECT s.id, t.id, '{EscapeSql(relationship.Relationship)}', c.id, {confidenceLiteral}, {evidenceLiteral} FROM entities s JOIN entities t ON t.name = '{EscapeSql(relationship.TargetName)}' {chunkJoin} WHERE s.name = '{EscapeSql(relationship.SourceName)}' ON CONFLICT (source_entity_id, target_entity_id, relationship_type, chunk_id) DO UPDATE SET confidence = EXCLUDED.confidence, evidence = EXCLUDED.evidence;");
            statements.Add($"SELECT * FROM ag_catalog.cypher('{EscapeSql(graphName)}', $$ MATCH (a), (b) WHERE a.name=\"{Escape(relationship.SourceName)}\" AND b.name=\"{Escape(relationship.TargetName)}\" MERGE (a)-[r:RELATION {{type:\"{Escape(relationship.Relationship)}\"}}]->(b) SET r.confidence={(relationship.Confidence.HasValue ? relationship.Confidence.Value.ToString("0.####", CultureInfo.InvariantCulture) : "null")}, r.chunk_id={(relationship.ChunkId.HasValue ? relationship.ChunkId.Value.ToString(CultureInfo.InvariantCulture) : "null")}, r.evidence={(string.IsNullOrWhiteSpace(relationship.Evidence) ? "null" : $"\"{Escape(relationship.Evidence)}\"")} RETURN r $$) as (r agtype);");
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

    private static DocumentMetadata NormalizeMetadata(DocumentMetadata? metadata)
    {
        if (metadata is null)
        {
            return new DocumentMetadata(null, null, Array.Empty<string>());
        }

        var documentType = string.IsNullOrWhiteSpace(metadata.DocumentType) ? null : metadata.DocumentType.Trim();
        var tags = metadata.Tags?
            .Select(tag => tag?.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        return metadata with
        {
            DocumentType = documentType,
            Tags = tags
        };
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

    private static string NormalizeRelationshipType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "related_to";
        }

        var normalized = new string(value.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        while (normalized.Contains("__", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(normalized.Trim('_')) ? "related_to" : normalized.Trim('_');
    }

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

    private static long CreateInitialDocumentSeed()
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1_000_000_000;

    private static long CreateDocumentId(string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return Interlocked.Increment(ref _documentSeed);
        }

        var normalized = contentHash.Trim().ToLowerInvariant();
        var hash = Regex.IsMatch(normalized, "^[0-9a-f]{16,}$", RegexOptions.IgnoreCase)
            ? normalized
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return ParseSafeIdentifier(hash);
    }

    private static long BuildChunkId(long documentId, int chunkIndex)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{documentId}:{chunkIndex}"))).ToLowerInvariant();
        return ParseSafeIdentifier(hash);
    }

    private static long ParseSafeIdentifier(string hexHash)
    {
        var value = long.Parse(hexHash[..13], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return value == 0 ? 1 : value;
    }

    private static string BuildExtensionStatement(string[] extensions, string extensionName)
        => extensions.Any(name => string.Equals(name, extensionName, StringComparison.OrdinalIgnoreCase))
            ? $"DO $$ BEGIN CREATE EXTENSION IF NOT EXISTS {extensionName}; EXCEPTION WHEN OTHERS THEN RAISE NOTICE '{extensionName} extension not available in this environment.'; END $$;"
            : $"DO $$ BEGIN RAISE NOTICE '{extensionName} extension disabled by configuration.'; END $$;";

    private static string BuildDiskAnnIndexStatement(string[] extensions)
        => extensions.Any(name => string.Equals(name, "pg_diskann", StringComparison.OrdinalIgnoreCase))
            ? "DO $$ BEGIN CREATE INDEX IF NOT EXISTS idx_chunks_embedding ON chunks USING diskann (embedding vector_cosine_ops); EXCEPTION WHEN OTHERS THEN RAISE NOTICE 'diskann index creation skipped.'; END $$;"
            : "DO $$ BEGIN RAISE NOTICE 'diskann index creation disabled by configuration.'; END $$;";

    private static string BuildEntityDiskAnnIndexStatement(string[] extensions)
        => extensions.Any(name => string.Equals(name, "pg_diskann", StringComparison.OrdinalIgnoreCase))
            ? "DO $$ BEGIN CREATE INDEX IF NOT EXISTS idx_entities_embedding ON entities USING diskann (embedding vector_cosine_ops); EXCEPTION WHEN OTHERS THEN RAISE NOTICE 'entity diskann index creation skipped.'; END $$;"
            : "DO $$ BEGIN RAISE NOTICE 'entity diskann index creation disabled by configuration.'; END $$;";

    private static string BuildTagsJsonLiteral(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return "NULL";
        }

        var json = JsonSerializer.Serialize(tags);
        return $"'{EscapeSql(json)}'::jsonb";
    }

    private static string BuildCypherTagsLiteral(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return "null";
        }

        return JsonSerializer.Serialize(tags);
    }

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
