CREATE EXTENSION IF NOT EXISTS age;
DO $$ BEGIN
    CREATE EXTENSION IF NOT EXISTS vector;
EXCEPTION
    WHEN OTHERS THEN
        RAISE NOTICE 'vector extension not available in this environment.';
END $$;
DO $$ BEGIN
    CREATE EXTENSION IF NOT EXISTS pg_diskann;
EXCEPTION
    WHEN OTHERS THEN
        RAISE NOTICE 'pg_diskann extension not available in this environment.';
END $$;

CREATE SCHEMA IF NOT EXISTS kg_data;
SET search_path = kg_data, public, ag_catalog;
SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM ag_catalog.ag_graph WHERE name = 'knowledge_graph') THEN create_graph('knowledge_graph') END;

CREATE TABLE IF NOT EXISTS kg_data.documents (
    id BIGINT PRIMARY KEY,
    title TEXT NOT NULL,
    file_name TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS kg_data.entities (
    id BIGSERIAL PRIMARY KEY,
    label TEXT NOT NULL,
    name TEXT NOT NULL UNIQUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS kg_data.chunks (
    id BIGSERIAL PRIMARY KEY,
    document_id BIGINT NOT NULL REFERENCES kg_data.documents(id) ON DELETE CASCADE,
    chunk_index INTEGER NOT NULL,
    content TEXT NOT NULL,
    embedding vector(1536),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (document_id, chunk_index)
);

CREATE TABLE IF NOT EXISTS kg_data.chunk_entities (
    chunk_id BIGINT NOT NULL REFERENCES kg_data.chunks(id) ON DELETE CASCADE,
    entity_id BIGINT NOT NULL REFERENCES kg_data.entities(id) ON DELETE CASCADE,
    PRIMARY KEY (chunk_id, entity_id)
);

CREATE INDEX IF NOT EXISTS idx_chunks_document ON kg_data.chunks(document_id, chunk_index);
CREATE INDEX IF NOT EXISTS idx_entities_name ON kg_data.entities(name);
CREATE INDEX IF NOT EXISTS idx_chunk_entities_entity_id ON kg_data.chunk_entities(entity_id);

DO $$ BEGIN
    CREATE INDEX IF NOT EXISTS idx_chunks_embedding ON kg_data.chunks USING diskann (embedding vector_cosine_ops);
EXCEPTION
    WHEN OTHERS THEN
        RAISE NOTICE 'diskann index creation skipped.';
END $$;
