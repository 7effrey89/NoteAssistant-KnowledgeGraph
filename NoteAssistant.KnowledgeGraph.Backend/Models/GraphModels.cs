namespace NoteAssistant.KnowledgeGraph.Backend.Models;

public sealed record ChunkDto(int Id, string Text);

public sealed record EntityDto(string Label, string Name);

public sealed record ChunkEntityLinkDto(int ChunkId, string EntityLabel, string EntityName);

public sealed record IngestionStatusDto(int DocumentId, string FileName, string State, DateTimeOffset UpdatedAt, string Message);

public sealed record GraphIngestionPlan(
    int DocumentId,
    string GraphName,
    string Title,
    IReadOnlyList<ChunkDto> Chunks,
    IReadOnlyList<EntityDto> Entities,
    IReadOnlyList<ChunkEntityLinkDto> Mentions,
    IReadOnlyList<string> SqlStatements,
    IngestionStatusDto Status);

public sealed record GraphQueryRequest(string Query, string GraphName = "knowledge_graph");

public sealed record GraphQueryResponse(
    bool Success,
    string? Error,
    IReadOnlyList<Dictionary<string, string?>> Rows,
    IReadOnlyList<GraphNodeDto> Nodes,
    IReadOnlyList<GraphEdgeDto> Edges);

public sealed record GraphNodeDto(string Id, string Label, string Title);

public sealed record GraphEdgeDto(string Source, string Target, string Label);

public sealed record QueryAssistantRequest(string Prompt);

public sealed record QueryAssistantResponse(string SuggestedCypher, string Explanation);
