using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public interface IMarkdownGraphIngestionService
{
    Task<GraphIngestionPlan> CreateGraphPlanAsync(string fileName, string markdownContent, DocumentMetadata? metadata, string contentHash, CancellationToken cancellationToken);
    GraphIngestionPlan ApplyDocumentIdentity(GraphIngestionPlan plan, string contentHash);
    GraphIngestionPlan RefreshSql(GraphIngestionPlan plan);
}
