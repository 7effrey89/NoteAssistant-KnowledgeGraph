namespace NoteAssistant.KnowledgeGraph.Backend.Models;

public sealed record ChunkDto(long Id, int ChunkIndex, string Text, float[]? Embedding = null);

public sealed record EntityDto(string Label, string Name, string? EmbeddingText = null, float[]? Embedding = null);

public sealed record RelationshipDto(string SourceName, string Relationship, string TargetName, double? Confidence = null);

public sealed record ChunkEntityLinkDto(long ChunkId, string EntityLabel, string EntityName);

public sealed record ChunkRelationshipDto(
    long? ChunkId,
    string SourceName,
    string Relationship,
    string TargetName,
    double? Confidence = null,
    string? Evidence = null);

public sealed record GraphExtractionDto(
    IReadOnlyList<EntityDto> Entities,
    IReadOnlyList<RelationshipDto> Relationships);

public sealed record IngestionStatusDto(long DocumentId, string FileName, string State, DateTimeOffset UpdatedAt, string Message);

public sealed record DocumentMetadata(
    string? DocumentType,
    DateOnly? DocumentDate,
    IReadOnlyList<string>? Tags);

public sealed record BulkMetadataUpdateRequest(
    IReadOnlyList<long> DocumentIds,
    string? DocumentType,
    string? DocumentDate,
    string? Tags);

public sealed record DocumentDecomposeRequest(
    string? DocumentType,
    string? DocumentDate,
    string? Tags);

public sealed record NoteAssistantMetadataFileDto(
    string FileName,
    string? NoteSession,
    string? FolderCreationDate,
    IReadOnlyList<string>? Customers,
    IReadOnlyList<string>? Services);

public sealed record NoteAssistantMetadataImportRequest(
    IReadOnlyList<NoteAssistantMetadataFileDto> Files);

public sealed record GraphIngestionPlan(
    long DocumentId,
    string GraphName,
    string Title,
    DocumentMetadata? Metadata,
    IReadOnlyList<ChunkDto> Chunks,
    IReadOnlyList<EntityDto> Entities,
    IReadOnlyList<ChunkEntityLinkDto> Mentions,
    IReadOnlyList<string> SqlStatements,
    IngestionStatusDto Status,
    string OriginalContent = "",
    string ContentHash = "",
    bool Cached = false,
    string DecompositionSystemPrompt = "",
    string DecompositionInputPrompt = "",
    IReadOnlyList<ChunkRelationshipDto>? Relationships = null);

public sealed record GraphQueryRequest(string Cypher, string GraphName = "knowledge_graph");

public sealed record GraphQueryResponse(
    bool Success,
    string? Error,
    IReadOnlyList<Dictionary<string, string?>> Rows,
    IReadOnlyList<GraphNodeDto> Nodes,
    IReadOnlyList<GraphEdgeDto> Edges);

public sealed record GraphNodeDto(string Id, string Label, string Title, IReadOnlyDictionary<string, string?>? Properties = null);

public sealed record GraphEdgeDto(string Source, string Target, string Label);

public sealed record GraphNodeDetailsRequest(
    string Id,
    string Label,
    string Title,
    IReadOnlyDictionary<string, string?>? Properties = null);

public sealed record GraphNodeDetailsResponse(
    bool Success,
    string? Error,
    string NodeType,
    IReadOnlyDictionary<string, string?> Attributes,
    IReadOnlyDictionary<string, string>? AttributeSources = null,
    IReadOnlyDictionary<string, string>? AttributeSourceSqls = null,
    IReadOnlyList<GraphNodeChunkDto>? Chunks = null);

public sealed record GraphNodeChunkDto(
    long Id,
    long DocumentId,
    int ChunkIndex,
    string Content,
    string? DocumentTitle,
    string? DocumentFileName,
    string? DocumentDate,
    string LinkReason,
    double? Score = null,
    double? Distance = null);

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
    string? ClarificationResponse = null,
    string? RetrievalMode = null);

public sealed record CommunityBuildResponse(
    bool Success,
    string? Error,
    int CommunitiesBuilt,
    int EntitiesAssigned,
    int RelationshipsUsed,
    HybridRetrievalTraceDto? Trace = null);

public sealed record CommunityBuildRequest(
    int Parallelism = 1,
    CommunityDetectionOptions? CommunityDetection = null);

public sealed record CommunityDetectionOptions(
    string Algorithm = "LeidenCpm",
    bool Directed = false,
    ulong Seed = 42,
    double CpmResolution = 0.25,
    double TypedRelationshipWeight = 2.0,
    double CoMentionWeight = 1.0,
    int MinCommunitySizeToSummarize = 2,
    int MaxCommunitiesToSummarize = 50);

public sealed record CommunityTuningProfile(
    string Id,
    DateTimeOffset CreatedAt,
    CommunityBuildRequest Config,
    double? ScorePercent = null,
    double? ConfidencePercent = null,
    string? Improvement = null,
    string Source = "manual");

public sealed record CommunityProfileSnapshot(
    string? ActiveProfileId,
    IReadOnlyList<CommunityTuningProfile> Profiles);

public sealed record SaveCommunityProfileRequest(
    CommunityBuildRequest Config,
    double? ScorePercent = null,
    double? ConfidencePercent = null,
    string? Improvement = null,
    string Source = "manual",
    bool MakeActive = true);

public sealed record SetActiveCommunityProfileRequest(string ProfileId);

public sealed record TuneCommunityProfileRequest(
    string? SystemPrompt,
    string? UserPrompt,
    CommunityBuildRequest CurrentConfig,
    bool PersistProfile = true,
    CommunityTuningAssessmentContext? AssessmentContext = null);

public sealed record CommunityTuningAssessmentContext(
    int? TotalCommunities = null,
    int? SingletonCommunities = null,
    int? MultiEntityCommunities = null,
    int? CandidateSummaryCount = null,
    string? Source = null);

public sealed record CommunityTuningScoreComponent(
    string Name,
    double Value,
    string Detail);

public sealed record CommunityTuningScoreBreakdown(
    string Method,
    double ScorePercent,
    double ConfidencePercent,
    IReadOnlyList<CommunityTuningScoreComponent> ScoreComponents,
    IReadOnlyList<CommunityTuningScoreComponent> ConfidenceComponents);

public sealed record CommunityTuningAgentResponse(
    bool Success,
    string? Error,
    CommunityBuildRequest? Config,
    double? ScorePercent = null,
    double? ConfidencePercent = null,
    string? Improvement = null,
    string? AgentResponse = null,
    HybridTokenUsageDto? TokenUsage = null,
    CommunityTuningProfile? SavedProfile = null,
    CommunityTuningScoreBreakdown? ScoreBreakdown = null);

public sealed record GlobalGraphRagRequest(
    string Query,
    int Limit = 6,
    bool IncludeTrace = false,
    bool IncludeAnswer = true,
    float[]? QueryEmbedding = null);

public sealed record GlobalCommunityResultDto(
    long Id,
    string Title,
    string Summary,
    double? Distance,
    int EntityCount,
    int RelationshipCount,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null);

public sealed record TemporalDocumentDto(
    long Id,
    string Title,
    DateOnly? DocumentDate,
    string? DocumentType,
    string? Tags);

public sealed record GlobalGraphRagResponse(
    bool Success,
    string? Error,
    IReadOnlyList<GlobalCommunityResultDto> Communities,
    IReadOnlyList<TemporalDocumentDto> Timeline,
    string PromptContext,
    string RetrievalOrder,
    string? Answer = null,
    HybridRetrievalTraceDto? Trace = null);

public sealed record HybridChunkResultDto(
    long Id,
    long DocumentId,
    int ChunkIndex,
    string Content,
    double? Distance,
    int? VectorRank = null,
    int? KeywordRank = null,
    double? Score = null,
    string? Source = null);

public sealed record HybridChunkSourceCountDto(string Source, int Count, double Percentage);

public sealed record HybridGraphRelationshipDto(
    string Source,
    string Relationship,
    string Target,
    long? DocumentId = null,
    int? ChunkIndex = null,
    string? SourceText = null);

public sealed record HybridTokenUsageDto(int? PromptTokens, int? CompletionTokens);

public sealed record HybridRetrievalTraceStepDto(
    string Name,
    string Summary,
    string Detail,
    int? DurationMs = null,
    HybridTokenUsageDto? TokenUsage = null);

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
    string? SystemPrompt = null,
    string? AnalysisSystemPrompt = null,
    IReadOnlyList<HybridGraphRelationshipDto>? GraphRelationships = null,
    string? ResolvedRetrievalMode = null,
    string? RetrievalModeRationale = null,
    IReadOnlyList<HybridChunkSourceCountDto>? ChunkSourceBreakdown = null,
    int? ResolvedTraversalHops = null);
