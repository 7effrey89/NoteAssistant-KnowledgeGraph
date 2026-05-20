using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public interface IMarkdownGraphIngestionService
{
    Task<GraphIngestionPlan> CreateGraphPlanAsync(string fileName, string markdownContent, DocumentMetadata? metadata, CancellationToken cancellationToken);
    GraphIngestionPlan RefreshSql(GraphIngestionPlan plan);
}
