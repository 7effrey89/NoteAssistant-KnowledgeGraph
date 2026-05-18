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
