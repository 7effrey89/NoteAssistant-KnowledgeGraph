namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public static class LeidenCommunityDetector
{
    public static List<HashSet<long>> DetectCommunities(IReadOnlyList<long> entityIds, IReadOnlyList<(long SourceId, long TargetId)> relationships)
    {
        var nodes = entityIds.Distinct().OrderBy(id => id).ToList();
        if (nodes.Count == 0)
        {
            return [];
        }

        var known = nodes.ToHashSet();
        var adjacency = nodes.ToDictionary(id => id, _ => new Dictionary<long, double>());
        foreach (var (sourceId, targetId) in relationships)
        {
            if (sourceId == targetId || !known.Contains(sourceId) || !known.Contains(targetId))
            {
                continue;
            }

            AddWeight(adjacency[sourceId], targetId, 1d);
            AddWeight(adjacency[targetId], sourceId, 1d);
        }

        var nodeDegree = adjacency.ToDictionary(pair => pair.Key, pair => pair.Value.Values.Sum());
        var totalEdgeWeightTimesTwo = nodeDegree.Values.Sum();
        if (totalEdgeWeightTimesTwo <= 0d)
        {
            return nodes.Select(node => new HashSet<long> { node }).ToList();
        }

        var communityByNode = nodes.ToDictionary(node => node, node => node);
        var communityDegree = nodeDegree.ToDictionary(pair => pair.Key, pair => pair.Value);

        const int maxIterations = 12;
        const double gainEpsilon = 1e-9;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var moved = false;
            foreach (var node in nodes)
            {
                var currentCommunity = communityByNode[node];
                var currentDegree = nodeDegree[node];
                if (currentDegree <= 0d)
                {
                    continue;
                }

                communityDegree[currentCommunity] -= currentDegree;

                var weightToCommunity = new Dictionary<long, double>();
                foreach (var (neighbor, weight) in adjacency[node])
                {
                    var neighborCommunity = communityByNode[neighbor];
                    AddWeight(weightToCommunity, neighborCommunity, weight);
                }

                var bestCommunity = currentCommunity;
                var bestGain = 0d;
                foreach (var (candidateCommunity, candidateWeight) in weightToCommunity)
                {
                    var gain = candidateWeight - (currentDegree * communityDegree[candidateCommunity] / totalEdgeWeightTimesTwo);
                    if (gain > bestGain + gainEpsilon ||
                        (Math.Abs(gain - bestGain) <= gainEpsilon && candidateCommunity < bestCommunity))
                    {
                        bestGain = gain;
                        bestCommunity = candidateCommunity;
                    }
                }

                communityByNode[node] = bestCommunity;
                communityDegree[bestCommunity] += currentDegree;
                if (bestCommunity != currentCommunity)
                {
                    moved = true;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        var communities = communityByNode
            .GroupBy(pair => pair.Value)
            .Select(group => group.Select(pair => pair.Key).ToHashSet())
            .ToList();

        return RefineDisconnectedCommunities(communities, adjacency);
    }

    private static List<HashSet<long>> RefineDisconnectedCommunities(IReadOnlyList<HashSet<long>> communities, IReadOnlyDictionary<long, Dictionary<long, double>> adjacency)
    {
        var refined = new List<HashSet<long>>();
        foreach (var community in communities)
        {
            var pending = new HashSet<long>(community);
            while (pending.Count > 0)
            {
                var seed = pending.Min();
                var component = new HashSet<long>();
                var stack = new Stack<long>();
                stack.Push(seed);
                pending.Remove(seed);

                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    component.Add(current);
                    foreach (var neighbor in adjacency[current].Keys)
                    {
                        if (!community.Contains(neighbor) || !pending.Remove(neighbor))
                        {
                            continue;
                        }

                        stack.Push(neighbor);
                    }
                }

                refined.Add(component);
            }
        }

        return refined
            .OrderByDescending(component => component.Count)
            .ThenBy(component => component.Min())
            .ToList();
    }

    private static void AddWeight(IDictionary<long, double> bucket, long key, double delta)
    {
        if (!bucket.TryAdd(key, delta))
        {
            bucket[key] += delta;
        }
    }
}
