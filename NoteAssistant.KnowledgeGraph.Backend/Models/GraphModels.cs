namespace NoteAssistant.KnowledgeGraph.Backend.Models;

public sealed record ChunkDto(int Id, int ChunkIndex, string Text, float[]? Embedding = null);

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
    IngestionStatusDto Status,
    string OriginalContent = "",
    string ContentHash = "",
    bool Cached = false);

public sealed record GraphQueryRequest(string Cypher, string GraphName = "knowledge_graph");

public sealed record GraphQueryResponse(
    bool Success,
    string? Error,
    IReadOnlyList<Dictionary<string, string?>> Rows,
    IReadOnlyList<GraphNodeDto> Nodes,
    IReadOnlyList<GraphEdgeDto> Edges);

public sealed record GraphNodeDto(string Id, string Label, string Title);

public sealed record GraphEdgeDto(string Source, string Target, string Label);

public sealed record StatementExecutionDto(int Index, string StatementType, bool Success, int DurationMs, string? Error, string Statement);

public sealed record IngestionExecutionLogDto(DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, int TotalStatements, int SucceededStatements, int FailedStatements, IReadOnlyList<StatementExecutionDto> Steps);

public sealed record QueryAssistantRequest(string Prompt);

public sealed record QueryAssistantResponse(string SuggestedCypher, string Explanation);

public sealed record HybridRetrievalRequest(
    string Query,
    int MaxHops = 2,
    int Limit = 10,
    string GraphName = "knowledge_graph",
    float[]? QueryEmbedding = null,
    bool IncludeTrace = false,
    bool IncludeAnswer = false,
    int ClarificationAttempts = 0,
    string? ClarificationResponse = null);

public sealed record HybridChunkResultDto(long Id, int DocumentId, int ChunkIndex, string Content, double? Distance);

public sealed record HybridRetrievalTraceStepDto(string Name, string Summary, string Detail, int? DurationMs = null);

public sealed record HybridRetrievalTraceDto(string Question, IReadOnlyList<HybridRetrievalTraceStepDto> Steps);

public sealed record HybridRetrievalResponse(
    bool Success,
    string? Error,
    IReadOnlyList<string> DetectedEntities,
    IReadOnlyList<string> GraphEntities,
    IReadOnlyList<string> MatchedEntities,
    IReadOnlyList<HybridChunkResultDto> Chunks,
    string PromptContext,
    string RetrievalOrder,
    string? Answer = null,
    HybridRetrievalTraceDto? Trace = null,
    string? ClarificationQuestion = null,
    string? RewrittenQuestion = null,
    string? SystemPrompt = null);
