using System.Collections.Concurrent;
using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public sealed class IngestionStore
{
    private readonly ConcurrentDictionary<int, IngestionStatusDto> _statuses = new();
    private readonly ConcurrentDictionary<int, GraphIngestionPlan> _plans = new();
    private readonly ConcurrentDictionary<int, IngestionExecutionLogDto> _executionLogs = new();

    public void Upsert(IngestionStatusDto status)
    {
        _statuses[status.DocumentId] = status with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    public IngestionStatusDto? Get(int documentId)
    {
        _statuses.TryGetValue(documentId, out var status);
        return status;
    }

    public void SavePlan(GraphIngestionPlan plan)
    {
        _plans[plan.DocumentId] = plan;
    }

    public GraphIngestionPlan? GetPlan(int documentId)
    {
        _plans.TryGetValue(documentId, out var plan);
        return plan;
    }

    public void SaveExecutionLog(int documentId, IngestionExecutionLogDto log)
    {
        _executionLogs[documentId] = log;
    }

    public IngestionExecutionLogDto? GetExecutionLog(int documentId)
    {
        _executionLogs.TryGetValue(documentId, out var log);
        return log;
    }
}
