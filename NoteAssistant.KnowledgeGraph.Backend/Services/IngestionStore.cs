using System.Collections.Concurrent;
using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public sealed class IngestionStore
{
    private readonly ConcurrentDictionary<long, IngestionStatusDto> _statuses = new();
    private readonly ConcurrentDictionary<long, GraphIngestionPlan> _plans = new();
    private readonly ConcurrentDictionary<long, IngestionExecutionLogDto> _executionLogs = new();

    public void Upsert(IngestionStatusDto status)
    {
        _statuses[status.DocumentId] = status with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    public IngestionStatusDto? Get(long documentId)
    {
        _statuses.TryGetValue(documentId, out var status);
        return status;
    }

    public void SavePlan(GraphIngestionPlan plan)
    {
        _plans[plan.DocumentId] = plan;
    }

    public GraphIngestionPlan? GetPlan(long documentId)
    {
        _plans.TryGetValue(documentId, out var plan);
        return plan;
    }

    public void SaveExecutionLog(long documentId, IngestionExecutionLogDto log)
    {
        _executionLogs[documentId] = log;
    }

    public IngestionExecutionLogDto? GetExecutionLog(long documentId)
    {
        _executionLogs.TryGetValue(documentId, out var log);
        return log;
    }
}
