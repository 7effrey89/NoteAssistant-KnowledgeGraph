# NoteAssistant-KnowledgeGraph

C# solution for a PostgreSQL + Apache AGE knowledge graph with:

1. **`NoteAssistant.KnowledgeGraph.Backend`**
   - Deployment assets for PostgreSQL + AGE
   - Markdown ingestion into hybrid GraphRAG model:
     - relational tables for `chunks` (+ embedding), `entities`, and `chunk_entities`
     - AGE graph for entity relationships
   - Query API + Cypher query assistant + hybrid retrieval pipeline
2. **`NoteAssistant.KnowledgeGraph.Web`**
   - Upload UI for `.md` documents
   - Decomposition/status view
   - Query editor + assistant panel
   - Interactive graph explorer for query results

## Project structure

- `/NoteAssistant.KnowledgeGraph.slnx`
- `/NoteAssistant.KnowledgeGraph.Backend`
  - `/Deployment/docker-compose.yml`
  - `/Deployment/init/01-age-init.sql`
- `/NoteAssistant.KnowledgeGraph.Web`

## Run PostgreSQL + Apache AGE

```bash
cd NoteAssistant.KnowledgeGraph.Backend/Deployment
docker compose up -d
```

This provisions:
- Database: `noteassistant`
- User: `postgres`
- Password: `postgres`
- Graph: `knowledge_graph`
- Extensions (when available): `age`, `vector`, `pg_diskann`

## Run backend API

```bash
cd NoteAssistant.KnowledgeGraph.Backend
dotnet run
```

Optional (if your database is not local default):

```bash
ConnectionStrings__AgeDatabase="Host=<host>;Port=5432;Database=noteassistant;Username=<user>;Password=<password>" dotnet run
```

Backend launch profile exposes Swagger and APIs like:
- `POST /api/documents/upload`
- `GET /api/documents/{documentId}/status`
- `POST /api/query`
- `POST /api/query/assist`
- `POST /api/retrieval/hybrid`

## Run web app

```bash
cd NoteAssistant.KnowledgeGraph.Web
dotnet run
```

Open the web URL, upload a markdown file, inspect chunks/entities/status, and execute graph queries.

## Database setup — step by step

Docker Compose (`Deployment/docker-compose.yml`) creates the `noteassistant` database automatically when the container starts. The init script (`Deployment/init/01-age-init.sql`) then runs every step below in sequence.

### Step 1 — Create the database

Docker Compose handles this via the `POSTGRES_DB` environment variable. If you're provisioning manually (e.g. Azure Flexible Server), use a failsafe command that creates the database only when it does not already exist:

```sql
SELECT 'CREATE DATABASE noteassistant'
WHERE NOT EXISTS (
    SELECT 1 FROM pg_database WHERE datname = 'noteassistant'
)\gexec
```

> `CREATE DATABASE IF NOT EXISTS` is not supported in PostgreSQL, so the `\gexec` pattern is the safe equivalent in `psql`.

### Step 2 — Enable extensions

```sql
-- Required: Apache AGE (property graph engine)
CREATE EXTENSION IF NOT EXISTS age;

-- Required: pgvector (vector storage + cosine distance operator)
CREATE EXTENSION IF NOT EXISTS vector;

-- Optional: pg_diskann (disk-based ANN index — better than HNSW at scale)
CREATE EXTENSION IF NOT EXISTS pg_diskann;
```

> `pg_diskann` is only available on Azure Database for PostgreSQL Flexible Server. The init script skips it gracefully when the extension is absent.

### Step 3 — Create the relational tables

Chunks (document text + vector embeddings) and entities live in standard Postgres tables for fast relational queries and vector search.

```sql
-- Source documents
CREATE TABLE IF NOT EXISTS documents (
    id          BIGINT       PRIMARY KEY,
    title       TEXT         NOT NULL,
    file_name   TEXT         NOT NULL,
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

-- Named entities extracted from documents (Company, Topic, Platform, …)
CREATE TABLE IF NOT EXISTS entities (
    id          BIGSERIAL    PRIMARY KEY,
    label       TEXT         NOT NULL,
    name        TEXT         NOT NULL UNIQUE,
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

-- Text chunks with a 1536-dimensional embedding column
CREATE TABLE IF NOT EXISTS chunks (
    id           BIGSERIAL  PRIMARY KEY,
    document_id  BIGINT     NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    chunk_index  INTEGER    NOT NULL,
    content      TEXT       NOT NULL,
    embedding    vector(1536),          -- populated after embedding generation
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (document_id, chunk_index)
);

-- Join table: which entities are mentioned in which chunks
CREATE TABLE IF NOT EXISTS chunk_entities (
    chunk_id   BIGINT NOT NULL REFERENCES chunks(id)    ON DELETE CASCADE,
    entity_id  BIGINT NOT NULL REFERENCES entities(id)  ON DELETE CASCADE,
    PRIMARY KEY (chunk_id, entity_id)
);
```

Indexes:

```sql
CREATE INDEX IF NOT EXISTS idx_chunks_document          ON chunks(document_id, chunk_index);
CREATE INDEX IF NOT EXISTS idx_entities_name            ON entities(name);
CREATE INDEX IF NOT EXISTS idx_chunk_entities_entity_id ON chunk_entities(entity_id);

-- DiskANN vector index (Azure only; falls back silently)
CREATE INDEX IF NOT EXISTS idx_chunks_embedding
    ON chunks USING diskann (embedding vector_cosine_ops);
```

### Step 4 — Create the AGE property graph

Apache AGE stores entity **relationships** as a property graph. The graph sits alongside the relational tables in the same database.

```sql
LOAD 'age';
SET search_path = ag_catalog, "$user", public;

-- Idempotent: only creates the graph if it does not already exist
SELECT CASE
    WHEN NOT EXISTS (
        SELECT 1 FROM ag_catalog.ag_graph WHERE name = 'knowledge_graph'
    )
    THEN create_graph('knowledge_graph')
END;
```

Each entity gets a corresponding **vertex** in the graph:

```sql
-- Example: upsert a Company node named "Microsoft"
SELECT * FROM cypher('knowledge_graph', $$
    MERGE (e:Company {name: "Microsoft"})
$$) AS (e agtype);
```

Relationships between entities that appear together in the same chunk are stored as edges:

```sql
-- Example: two entities that co-occur in a chunk get a RELATED_TO edge
SELECT * FROM cypher('knowledge_graph', $$
    MATCH (a {name: "Microsoft"}), (b {name: "OpenAI"})
    MERGE (a)-[:RELATED_TO]->(b)
$$) AS (v agtype);
```

### Step 5 — Link the graph to the relational tables

The `chunk_entities` join table is the bridge between the two stores. A chunk row in Postgres references an entity row, and that same entity has a vertex in the AGE graph (matched by `name`).

```
chunks (Postgres)
  └─ chunk_entities (join)
       └─ entities (Postgres)  ←── name ───→  vertex in knowledge_graph (AGE)
```

**Inserting a chunk-entity link** (used during ingestion):

```sql
-- Link chunk (document_id=1, chunk_index=1) to entity named "Microsoft"
INSERT INTO chunk_entities (chunk_id, entity_id)
SELECT c.id, e.id
FROM   chunks   c
JOIN   entities e ON e.name = 'Microsoft'
WHERE  c.document_id = 1
AND    c.chunk_index  = 1
ON CONFLICT DO NOTHING;
```

**Hybrid retrieval query** (graph-filtered vector search):

```sql
-- Step A: expand entities via AGE graph traversal (1-2 hops from seed)
SELECT * FROM cypher('knowledge_graph', $$
    MATCH (a {name: "Microsoft"})-[*1..2]-(b)
    RETURN DISTINCT b
    LIMIT 50
$$) AS (node agtype);

-- Step B: use the entity names returned above to filter chunks,
--         then rank by cosine distance to the query embedding
SELECT  c.id,
        c.document_id,
        c.chunk_index,
        c.content,
        c.embedding <=> '[/* 1536-dim query vector */]'::vector AS distance
FROM    chunks        c
JOIN    chunk_entities ce ON ce.chunk_id  = c.id
JOIN    entities       e  ON e.id         = ce.entity_id
WHERE   e.name = ANY(ARRAY['Microsoft', 'OpenAI', /* … graph results … */])
  AND   c.embedding IS NOT NULL
ORDER BY distance
LIMIT 10;
```

> This is the **GraphRAG magic**: the graph narrows which chunks are candidates; the vector distance ranks them by semantic relevance to the question.

---

## Hybrid retrieval flow (GraphRAG)

1. **Entity detection** from question text
2. **Graph traversal in AGE** to expand relevant entities
3. **Graph-filtered vector search** over relational `chunks` table
4. **Prompt context assembly** for final LLM answer

Order is intentionally:

`Graph -> Vector -> LLM`

and not `Vector -> Graph`.

## Example Cypher query for the web query editor

```cypher
MATCH p=(n)-[r]->(m)
RETURN n, r, m
LIMIT 50
```
