namespace CognitiveEngine.Infrastructure.Storage.Graph;

using System.Collections.Concurrent;
using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Domain.Models;

/// <summary>
/// Высокопроизводительное In-Memory хранилище графа знаний с двусторонней индексацией,
/// O(1) верификацией ребер и атомарной пакетной вставкой без взаимных блокировок.
/// </summary>
public sealed class InMemoryGraphStore : IGraphStorage
{
    private readonly ConcurrentDictionary<string, GraphNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<GraphEdge>> _outEdges = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<GraphEdge>> _inEdges = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, GraphEdge> _allEdges = new();
    private readonly ConcurrentDictionary<(string Source, string Target), GraphEdge> _fastEdgeLookup = new();

    public string Domain { get; }

    public InMemoryGraphStore(string domain = "code_engine")
    {
        Domain = domain;
    }

    public Task<GraphNode?> GetNodeAsync(string id, CancellationToken ct = default)
    {
        _nodes.TryGetValue(id, out var node);
        return Task.FromResult(node);
    }

    public Task<IReadOnlyList<GraphNode>> GetAllNodesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<GraphNode> list = _nodes.Values.ToList();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<GraphNode>> FindSimilarAsync(
        ReadOnlyMemory<float> queryVector,
        int topK = 5,
        CancellationToken ct = default)
    {
        if (queryVector.IsEmpty || _nodes.IsEmpty)
            return Task.FromResult<IReadOnlyList<GraphNode>>(Array.Empty<GraphNode>());

        var querySpan = queryVector.Span;
        int safeCapacity = Math.Min(topK, Math.Max(_nodes.Count, 16));
        var candidates = new PriorityQueue<GraphNode, float>(safeCapacity);

        foreach (var node in _nodes.Values)
        {
            if (node.Vector.IsEmpty || node.Vector.Length != querySpan.Length)
                continue;

            float cosineSimilarity = CalculateCosineSimilarity(querySpan, node.Vector.Span);

            if (candidates.Count < topK)
            {
                candidates.Enqueue(node, cosineSimilarity);
            }
            else if (candidates.TryPeek(out _, out float lowestScore) && cosineSimilarity > lowestScore)
            {
                candidates.Dequeue();
                candidates.Enqueue(node, cosineSimilarity);
            }
        }

        var results = new List<GraphNode>(candidates.Count);
        while (candidates.TryDequeue(out var node, out _))
        {
            results.Add(node);
        }

        results.Reverse();
        return Task.FromResult<IReadOnlyList<GraphNode>>(results);
    }

    private static float CalculateCosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;

        float dot = 0f;
        float magA = 0f;
        float magB = 0f;

        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        float denom = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return denom > 0f ? dot / denom : 0f;
    }

    public Task<IReadOnlyList<GraphEdge>> GetLocalFrontierAsync(
        IEnumerable<string> nodeIds,
        CancellationToken ct = default)
    {
        var frontier = new List<GraphEdge>();
        var seenEdgeIds = new HashSet<Guid>();

        foreach (var nid in nodeIds)
        {
            if (_outEdges.TryGetValue(nid, out var outgoing))
            {
                lock (outgoing)
                {
                    foreach (var edge in outgoing)
                    {
                        if (seenEdgeIds.Add(edge.Id))
                            frontier.Add(edge);
                    }
                }
            }

            if (_inEdges.TryGetValue(nid, out var incoming))
            {
                lock (incoming)
                {
                    foreach (var edge in incoming)
                    {
                        if (seenEdgeIds.Add(edge.Id))
                            frontier.Add(edge);
                    }
                }
            }
        }

        return Task.FromResult<IReadOnlyList<GraphEdge>>(frontier);
    }

    public Task<IReadOnlyList<GraphEdge>> GetAllEdgesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<GraphEdge> edges = _allEdges.Values.ToList();
        return Task.FromResult(edges);
    }

    public Task AddNodesBatchAsync(IEnumerable<GraphNode> nodes, CancellationToken ct = default)
    {
        foreach (var node in nodes)
        {
            _nodes[node.Id] = node;
        }

        return Task.CompletedTask;
    }

    public Task AddEdgeAsync(GraphEdge edge, CancellationToken ct = default)
    {
        InsertEdgeInternal(edge);
        return Task.CompletedTask;
    }

    public Task AddEdgesBatchAsync(IEnumerable<GraphEdge> edges, CancellationToken ct = default)
    {
        foreach (var edge in edges)
        {
            InsertEdgeInternal(edge);
        }

        return Task.CompletedTask;
    }

    public Task<GraphEdge?> FindEdgeAsync(string source, string target, CancellationToken ct = default)
    {
        var key = (source.ToLowerInvariant(), target.ToLowerInvariant());
        if (_fastEdgeLookup.TryGetValue(key, out var edge))
        {
            return Task.FromResult<GraphEdge?>(edge);
        }

        // Для Contains проверяем обратное отношение
        var reverseKey = (target.ToLowerInvariant(), source.ToLowerInvariant());
        if (_fastEdgeLookup.TryGetValue(reverseKey, out var reverseEdge) && reverseEdge.Relation == RelationType.Contains)
        {
            return Task.FromResult<GraphEdge?>(reverseEdge);
        }

        return Task.FromResult<GraphEdge?>(null);
    }

    public Task ReinforceClaimAsync(
        string source,
        string target,
        RelationType relation,
        CancellationToken ct = default)
    {
        if (_outEdges.TryGetValue(source, out var outgoing))
        {
            lock (outgoing)
            {
                for (int i = 0; i < outgoing.Count; i++)
                {
                    var edge = outgoing[i];
                    if (edge.Target.Equals(target, StringComparison.OrdinalIgnoreCase) && edge.Relation == relation)
                    {
                        var updated = edge with
                        {
                            Weight = Math.Clamp(edge.Weight + 0.05f, 0.0f, 1.0f),
                            Confirmations = edge.Confirmations + 1,
                            Activations = edge.Activations + 1
                        };

                        outgoing[i] = updated;
                        _allEdges[edge.Id] = updated;
                        _fastEdgeLookup[(source.ToLowerInvariant(), target.ToLowerInvariant())] = updated;
                        break;
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    private void InsertEdgeInternal(GraphEdge edge)
    {
        _allEdges[edge.Id] = edge;
        _fastEdgeLookup[(edge.Source.ToLowerInvariant(), edge.Target.ToLowerInvariant())] = edge;

        _outEdges.AddOrUpdate(
            edge.Source,
            _ => [edge],
            (_, list) =>
            {
                lock (list) { list.Add(edge); }
                return list;
            });

        _inEdges.AddOrUpdate(
            edge.Target,
            _ => [edge],
            (_, list) =>
            {
                lock (list) { list.Add(edge); }
                return list;
            });
    }
}