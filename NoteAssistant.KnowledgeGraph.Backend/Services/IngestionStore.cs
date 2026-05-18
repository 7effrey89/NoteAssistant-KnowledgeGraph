using System.Collections.Concurrent;
using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public sealed class IngestionStore
{
    private readonly ConcurrentDictionary<int, IngestionStatusDto> _statuses = new();

    public void Upsert(IngestionStatusDto status)
    {
        _statuses[status.DocumentId] = status with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    public IngestionStatusDto? Get(int documentId)
    {
        _statuses.TryGetValue(documentId, out var status);
        return status;
    }
}
