using NoteAssistant.KnowledgeGraph.Backend.Models;
using NoteAssistant.KnowledgeGraph.Backend.Services;
using Npgsql;

namespace NoteAssistant.KnowledgeGraph.Backend.Tests;

public sealed class FakeFoundryInferenceClient : IFoundryInferenceClient
{
    public bool IsConfigured { get; init; } = true;
    public IReadOnlyList<float[]> Embeddings { get; init; } = Array.Empty<float[]>();
    public IReadOnlyList<EntityDto> Entities { get; init; } = Array.Empty<EntityDto>();

    public Task<float[]> CreateEmbeddingAsync(string input, CancellationToken cancellationToken)
        => Task.FromResult(Embeddings.FirstOrDefault() ?? new float[1536]);

    public Task<IReadOnlyList<float[]>> CreateEmbeddingsAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        if (Embeddings.Count == inputs.Count)
        {
            return Task.FromResult(Embeddings);
        }

        var vectors = inputs.Select(_ => new float[1536]).ToArray();
        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    public Task<IReadOnlyList<EntityDto>> ExtractEntitiesAsync(string markdownContent, CancellationToken cancellationToken)
        => Task.FromResult(Entities);
}

public sealed class StubAgeDatabaseConnectionFactory : IAgeDatabaseConnectionFactory
{
    public bool IsConfigured { get; init; }

    public Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
        => throw new InvalidOperationException("Stub connection factory does not open connections.");
}
