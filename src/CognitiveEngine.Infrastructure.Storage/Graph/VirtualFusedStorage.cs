namespace CognitiveEngine.Infrastructure.Storage.Graph;

using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Domain.Models;

/// <summary>
/// Федеративное виртуальное хранилище: агрегирует несколько изолированных графовых хранилищ.
/// </summary>
public sealed class VirtualFusedStorage : IGraphStorage
{
    private readonly IReadOnlyList<IGraphStorage> _storages;

    public string Domain { get; }

    public VirtualFusedStorage(IEnumerable<IGraphStorage> storages, string domain = "fused")
    {
        _storages = storages.ToList();
        Domain = domain;
    }

    public async Task<GraphNode?> GetNodeAsync(string id, CancellationToken ct = default)
    {
        foreach (var storage in _storages)
        {
            var node = await storage.GetNodeAsync(id, ct);
            if (node != null) return node;
        }
        return null;
    }

    public async Task<IReadOnlyList<GraphNode>> GetAllNodesAsync(CancellationToken ct = default)
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<GraphNode>();

        foreach (var storage in _storages)
        {
            var nodes = await storage.GetAllNodesAsync(ct);
            foreach (var node in nodes)
            {
                if (seenIds.Add(node.Id))
                {
                    results.Add(node);
                }
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<GraphNode>> FindSimilarAsync(
        ReadOnlyMemory<float> queryVector,
        int topK = 5,
        CancellationToken ct = default)
    {
        var candidates = new List<GraphNode>();
        foreach (var storage in _storages)
        {
            var similar = await storage.FindSimilarAsync(queryVector, topK, ct);
            candidates.AddRange(similar);
        }

        return candidates
            .DistinctBy(n => n.Id)
            .Take(topK)
            .ToList();
    }

    public async Task<IReadOnlyList<GraphEdge>> GetLocalFrontierAsync(
        IEnumerable<string> nodeIds,
        CancellationToken ct = default)
    {
        var seenEdgeIds = new HashSet<Guid>();
        var results = new List<GraphEdge>();

        foreach (var storage in _storages)
        {
            var frontier = await storage.GetLocalFrontierAsync(nodeIds, ct);
            foreach (var edge in frontier)
            {
                if (seenEdgeIds.Add(edge.Id))
                {
                    results.Add(edge);
                }
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<GraphEdge>> GetAllEdgesAsync(CancellationToken ct = default)
    {
        var seenEdgeIds = new HashSet<Guid>();
        var results = new List<GraphEdge>();

        foreach (var storage in _storages)
        {
            var edges = await storage.GetAllEdgesAsync(ct);
            foreach (var edge in edges)
            {
                if (seenEdgeIds.Add(edge.Id))
                {
                    results.Add(edge);
                }
            }
        }

        return results;
    }

    public async Task AddNodesBatchAsync(IEnumerable<GraphNode> nodes, CancellationToken ct = default)
    {
        if (_storages.Count > 0)
        {
            await _storages[0].AddNodesBatchAsync(nodes, ct);
        }
    }

    public async Task AddEdgeAsync(GraphEdge edge, CancellationToken ct = default)
    {
        if (_storages.Count > 0)
        {
            await _storages[0].AddEdgeAsync(edge, ct);
        }
    }

    public async Task AddEdgesBatchAsync(IEnumerable<GraphEdge> edges, CancellationToken ct = default)
    {
        if (_storages.Count > 0)
        {
            await _storages[0].AddEdgesBatchAsync(edges, ct);
        }
    }

    public async Task<GraphEdge?> FindEdgeAsync(string source, string target, CancellationToken ct = default)
    {
        foreach (var storage in _storages)
        {
            var edge = await storage.FindEdgeAsync(source, target, ct);
            if (edge != null) return edge;
        }

        return null;
    }

    public async Task ReinforceClaimAsync(
        string source,
        string target,
        RelationType relation,
        CancellationToken ct = default)
    {
        foreach (var storage in _storages)
        {
            await storage.ReinforceClaimAsync(source, target, relation, ct);
        }
    }
}