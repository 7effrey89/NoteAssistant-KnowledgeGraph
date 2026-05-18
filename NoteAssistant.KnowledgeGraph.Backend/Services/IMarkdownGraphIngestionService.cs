using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public interface IMarkdownGraphIngestionService
{
    GraphIngestionPlan CreateGraphPlan(string fileName, string markdownContent);
}
