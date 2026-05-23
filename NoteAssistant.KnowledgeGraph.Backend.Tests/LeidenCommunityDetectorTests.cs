using NoteAssistant.KnowledgeGraph.Backend.Services;
using Xunit;

namespace NoteAssistant.KnowledgeGraph.Backend.Tests;

public sealed class LeidenCommunityDetectorTests
{
    [Fact]
    public void DetectCommunities_SplitsBridgeConnectedCliques()
    {
        var entities = Enumerable.Range(1, 6).Select(id => (long)id).ToList();
        var relationships = new List<(long SourceId, long TargetId)>
        {
            (1, 2), (2, 3), (1, 3),
            (4, 5), (5, 6), (4, 6),
            (3, 4)
        };

        var communities = LeidenCommunityDetector.DetectCommunities(entities, relationships);

        Assert.Equal(2, communities.Count);
        Assert.Contains(communities, c => c.SetEquals([1L, 2L, 3L]));
        Assert.Contains(communities, c => c.SetEquals([4L, 5L, 6L]));
    }

    [Fact]
    public void DetectCommunities_ReturnsSingletonsWhenNoEdgesExist()
    {
        var entities = new List<long> { 10, 20, 30 };

        var communities = LeidenCommunityDetector.DetectCommunities(entities, []);

        Assert.Equal(3, communities.Count);
        Assert.All(entities, id => Assert.Contains(communities, c => c.SetEquals([id])));
    }
}
