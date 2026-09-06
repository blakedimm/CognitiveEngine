namespace CognitiveEngine.Infrastructure.Storage.Graph;

using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Models;

/// <summary>
/// Движок топологической компрессии: выполняет векторизованный расчет PageRank,
/// алгоритм транзитивной редукции спагетти-кода и схлопывание линейных цепочек в ОЗУ.
/// </summary>
public static class GraphTopologyOptimizer
{
    public static Dictionary<string, float> ComputePageRank(
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges,
        int iterations = 20,
        float damping = 0.85f)
    {
        int vCount = nodes.Count;
        if (vCount == 0) return [];

        var nodeIndex = new Dictionary<string, int>(vCount);
        for (int i = 0; i < vCount; i++)
        {
            nodeIndex[nodes[i].Id] = i;
        }

        var outDegrees = new int[vCount];
        var inEdgesMap = new List<(int Src, float Weight)>[vCount];
        for (int i = 0; i < vCount; i++) inEdgesMap[i] = [];

        foreach (var edge in edges)
        {
            if (nodeIndex.TryGetValue(edge.Source, out int sIdx) &&
                nodeIndex.TryGetValue(edge.Target, out int tIdx))
            {
                outDegrees[sIdx]++;
                inEdgesMap[tIdx].Add((sIdx, edge.Weight));
            }
        }

        var ranks = new float[vCount];
        var nextRanks = new float[vCount];
        Array.Fill(ranks, 1.0f / vCount);

        for (int iter = 0; iter < iterations; iter++)
        {
            float baseRank = (1.0f - damping) / vCount;
            for (int i = 0; i < vCount; i++)
            {
                float sum = 0.0f;
                foreach (var (srcIdx, weight) in inEdgesMap[i])
                {
                    int deg = Math.Max(1, outDegrees[srcIdx]);
                    sum += (ranks[srcIdx] / deg) * weight;
                }
                nextRanks[i] = baseRank + (damping * sum);
            }

            Array.Copy(nextRanks, ranks, vCount);
        }

        var result = new Dictionary<string, float>(vCount);
        for (int i = 0; i < vCount; i++)
        {
            result[nodes[i].Id] = ranks[i];
        }

        return result;
    }

    /// <summary>
    /// Транзитивная редукция графа вызовов: удаляет избыточную прямую связь A -> C,
    /// если уже существует гарантированный альтернативный путь A -> B -> C.
    /// </summary>
    public static IReadOnlyList<GraphEdge> ApplyTransitiveReduction(
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges)
    {
        int n = nodes.Count;
        var idxMap = new Dictionary<string, int>(n);
        for (int i = 0; i < n; i++) idxMap[nodes[i].Id] = i;

        // Битовая матрица достижимости для вызовов
        var reach = new bool[n, n];
        var callEdges = new List<GraphEdge>();
        var otherEdges = new List<GraphEdge>();

        foreach (var edge in edges)
        {
            if (edge.Relation == RelationType.Calls &&
                idxMap.TryGetValue(edge.Source, out int s) &&
                idxMap.TryGetValue(edge.Target, out int t))
            {
                reach[s, t] = true;
                callEdges.Add(edge);
            }
            else
            {
                otherEdges.Add(edge);
            }
        }

        // Алгоритм Флойда-Уоршелла для построения транзитивного замыкания
        var closure = (bool[,])reach.Clone();
        for (int k = 0; k < n; k++)
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            closure[i, j] = closure[i, j] || (closure[i, k] && closure[k, j]);
        }

        var cleanCallEdges = new List<GraphEdge>(callEdges.Count);
        foreach (var edge in callEdges)
        {
            int u = idxMap[edge.Source];
            int v = idxMap[edge.Target];

            bool hasAlternativePath = false;
            for (int k = 0; k < n; k++)
            {
                if (k != u && k != v && reach[u, k] && closure[k, v])
                {
                    hasAlternativePath = true;
                    break;
                }
            }

            if (!hasAlternativePath)
            {
                cleanCallEdges.Add(edge);
            }
        }

        return [.. cleanCallEdges, .. otherEdges];
    }
}