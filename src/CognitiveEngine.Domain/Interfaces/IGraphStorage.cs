namespace CognitiveEngine.Domain.Interfaces;

using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Models;

public interface IGraphStorage
{
    string Domain { get; }
    Task<GraphNode?> GetNodeAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<GraphNode>> GetAllNodesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GraphNode>> FindSimilarAsync(ReadOnlyMemory<float> queryVector, int topK = 5, CancellationToken ct = default);
    Task<IReadOnlyList<GraphEdge>> GetLocalFrontierAsync(IEnumerable<string> nodeIds, CancellationToken ct = default);
    Task<IReadOnlyList<GraphEdge>> GetAllEdgesAsync(CancellationToken ct = default);
    Task AddNodesBatchAsync(IEnumerable<GraphNode> nodes, CancellationToken ct = default);
    Task AddEdgeAsync(GraphEdge edge, CancellationToken ct = default);
    Task AddEdgesBatchAsync(IEnumerable<GraphEdge> edges, CancellationToken ct = default);
    Task<GraphEdge?> FindEdgeAsync(string source, string target, CancellationToken ct = default);
    Task ReinforceClaimAsync(string source, string target, RelationType relation, CancellationToken ct = default);
}