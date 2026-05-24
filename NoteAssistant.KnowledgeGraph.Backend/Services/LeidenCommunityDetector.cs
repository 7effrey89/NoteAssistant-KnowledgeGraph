using Libleidenalg;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public static class LeidenCommunityDetector
{
    private const double DefaultCpmResolution = 0.25d;
    private const ulong DefaultSeed = 42UL;

    public static List<HashSet<long>> DetectCommunities(IReadOnlyList<long> entityIds, IReadOnlyList<(long SourceId, long TargetId)> relationships)
        => DetectCommunities(
            entityIds,
            relationships.Select(relationship => (relationship.SourceId, relationship.TargetId, Weight: 1d)).ToList());

    public static List<HashSet<long>> DetectCommunities(
        IReadOnlyList<long> entityIds,
        IReadOnlyList<(long SourceId, long TargetId, double Weight)> relationships,
        double resolution = DefaultCpmResolution,
        ulong seed = DefaultSeed,
        bool directed = false)
    {
        var nodes = entityIds.OrderBy(id => id).ToList();
        if (nodes.Count != nodes.Distinct().Count())
        {
            throw new ArgumentException("Entity IDs must be unique.", nameof(entityIds));
        }

        if (nodes.Count == 0)
        {
            return [];
        }

        var indexById = nodes
            .Select((id, index) => (id, index))
            .ToDictionary(pair => pair.id, pair => pair.index);
        var weightedEdges = AggregateEdges(relationships, indexById, directed);
        if (weightedEdges.Count == 0)
        {
            return nodes.Select(node => new HashSet<long> { node }).ToList();
        }

        var membership = LeidenCpm.Partition(
            vertexCount: nodes.Count,
            edges: weightedEdges,
            directed: directed,
            resolution: resolution,
            seed: seed,
            out _);

        return membership
            .Select((communityId, nodeIndex) => new { communityId, entityId = nodes[nodeIndex] })
            .GroupBy(item => item.communityId)
            .Select(group => group.Select(item => item.entityId).ToHashSet())
            .OrderByDescending(component => component.Count)
            .ThenBy(component => component.Min())
            .ToList();
    }

    private static List<(int From, int To, double Weight)> AggregateEdges(
        IReadOnlyList<(long SourceId, long TargetId, double Weight)> relationships,
        IReadOnlyDictionary<long, int> indexById,
        bool directed)
    {
        var weights = new Dictionary<(int From, int To), double>();
        foreach (var (sourceId, targetId, weight) in relationships)
        {
            if (sourceId == targetId || weight <= 0d || double.IsNaN(weight) || double.IsInfinity(weight))
            {
                continue;
            }

            if (!indexById.TryGetValue(sourceId, out var sourceIndex) || !indexById.TryGetValue(targetId, out var targetIndex))
            {
                continue;
            }

            var from = directed ? sourceIndex : Math.Min(sourceIndex, targetIndex);
            var to = directed ? targetIndex : Math.Max(sourceIndex, targetIndex);
            var key = (from, to);
            weights[key] = weights.TryGetValue(key, out var existing) ? existing + weight : weight;
        }

        return weights
            .Select(pair => (pair.Key.From, pair.Key.To, pair.Value))
            .ToList();
    }
}
