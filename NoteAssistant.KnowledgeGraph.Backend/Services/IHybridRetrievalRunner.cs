using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public interface IHybridRetrievalRunner
{
    Task<HybridRetrievalResponse> ExecuteAsync(HybridRetrievalRequest request, CancellationToken cancellationToken);
}

public sealed class HybridRetrievalRunner(AgeGraphRepository repository) : IHybridRetrievalRunner
{
    public Task<HybridRetrievalResponse> ExecuteAsync(HybridRetrievalRequest request, CancellationToken cancellationToken)
        => repository.ExecuteHybridRetrievalAsync(request, cancellationToken);
}
