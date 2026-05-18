# NoteAssistant-KnowledgeGraph

C# solution for a PostgreSQL + Apache AGE knowledge graph with:

1. **`NoteAssistant.KnowledgeGraph.Backend`**
   - Deployment assets for PostgreSQL + AGE
   - Markdown ingestion into graph structures (`Document`, `Chunk`, entities)
   - Query API + Cypher query assistant
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
cd /home/runner/work/NoteAssistant-KnowledgeGraph/NoteAssistant-KnowledgeGraph/NoteAssistant.KnowledgeGraph.Backend/Deployment
docker compose up -d
```

This provisions:
- Database: `noteassistant`
- User: `postgres`
- Password: `postgres`
- Graph: `knowledge_graph`

## Run backend API

```bash
cd /home/runner/work/NoteAssistant-KnowledgeGraph/NoteAssistant-KnowledgeGraph/NoteAssistant.KnowledgeGraph.Backend
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

## Run web app

```bash
cd /home/runner/work/NoteAssistant-KnowledgeGraph/NoteAssistant-KnowledgeGraph/NoteAssistant.KnowledgeGraph.Web
dotnet run
```

Open the web URL, upload a markdown file, inspect chunks/entities/status, and execute graph queries.

## Example AGE query

```sql
SELECT *
FROM cypher('knowledge_graph', $$
    MATCH p=(n)-[r]->(m)
    RETURN n, r, m
    LIMIT 50
$$) AS (n agtype, r agtype, m agtype);
```
