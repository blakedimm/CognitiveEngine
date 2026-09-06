namespace CognitiveEngine.Core.Search.Engines;

using System.Collections.Concurrent;
using CognitiveEngine.Core.Search.Heuristics;
using CognitiveEngine.Core.Search.Tree;
using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Domain.Models;

public sealed class HierarchicalMctsEngine
{
    private readonly IGraphStorage _storage;
    private readonly IGnnPredictor _gnnPredictor;
    private readonly ConcurrentDictionary<string, byte> _visitedNodes = new();
    private readonly double _cPuct;

    public HierarchicalMctsEngine(IGraphStorage storage, IGnnPredictor gnnPredictor, double cPuct = 1.41)
    {
        _storage = storage;
        _gnnPredictor = gnnPredictor;
        _cPuct = cPuct;
    }

    public void ResetSession() => _visitedNodes.Clear();

    public async Task<IReadOnlyList<GraphEdge>> RunBidirectionalSearchAsync(
        IReadOnlyList<string> forwardSeeds,
        IReadOnlyList<string> backwardSeeds,
        int maxIterations = 350,
        bool isTraceMode = false,
        CancellationToken ct = default)
    {
        if (forwardSeeds.Count == 0) return [];

        string startSeed = forwardSeeds[0];
        string endSeed = backwardSeeds.Count > 0 ? backwardSeeds[0] : forwardSeeds[0];

        var forwardRoot = new MctsNode(startSeed, isForward: true);
        var backwardRoot = new MctsNode(endSeed, isForward: false);

        var forwardTree = new Dictionary<string, MctsNode> { [forwardRoot.Id] = forwardRoot };
        var backwardTree = new Dictionary<string, MctsNode> { [backwardRoot.Id] = backwardRoot };

        string? meetingNode = null;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            if (ct.IsCancellationRequested) break;

            // 1. Прямой луч
            var fNode = PuctCalculator.SelectPromisingNode(forwardRoot, _cPuct);
            if (!fNode.IsExpanded)
            {
                var frontier = await _storage.GetLocalFrontierAsync([fNode.Id], ct);
                foreach (var edge in frontier)
                {
                    string nextId = edge.Source == fNode.Id ? edge.Target : edge.Source;
                    if (fNode.VisitedNodes.Contains(nextId)) continue;

                    // Запрет Гудхарта: два Contains подряд полностью блокируются
                    if (edge.Relation == RelationType.Contains && fNode.ParentEdge?.Relation == RelationType.Contains)
                        continue;

                    float edgePrior = edge.Relation switch
                    {
                        RelationType.Calls => 2.0f,
                        RelationType.ExecTrace => 1.8f,
                        RelationType.Mutates => 1.5f,
                        RelationType.Contains => 0.15f, // Низкий приоритет туннеля
                        _ => 0.5f
                    };

                    var child = fNode.AddChild(nextId, edge, edgePrior);
                    forwardTree.TryAdd(nextId, child);

                    if (backwardTree.ContainsKey(nextId))
                    {
                        meetingNode = nextId;
                        break;
                    }
                }
                fNode.IsExpanded = true;
            }

            if (meetingNode != null) break;

            // 2. Обратный луч
            if (startSeed != endSeed)
            {
                var bNode = PuctCalculator.SelectPromisingNode(backwardRoot, _cPuct);
                if (!bNode.IsExpanded)
                {
                    var frontier = await _storage.GetLocalFrontierAsync([bNode.Id], ct);
                    foreach (var edge in frontier)
                    {
                        string prevId = edge.Target == bNode.Id ? edge.Source : edge.Target;
                        if (bNode.VisitedNodes.Contains(prevId)) continue;

                        if (edge.Relation == RelationType.Contains && bNode.ParentEdge?.Relation == RelationType.Contains)
                            continue;

                        float edgePrior = edge.Relation switch
                        {
                            RelationType.Calls => 2.0f,
                            RelationType.ExecTrace => 1.8f,
                            _ => 0.2f
                        };

                        var child = bNode.AddChild(prevId, edge, edgePrior);
                        backwardTree.TryAdd(prevId, child);

                        if (forwardTree.ContainsKey(prevId))
                        {
                            meetingNode = prevId;
                            break;
                        }
                    }
                    bNode.IsExpanded = true;
                }
            }

            if (meetingNode != null) break;
        }

        if (meetingNode != null)
        {
            var path = new List<GraphEdge>();
            var curr = forwardTree[meetingNode];
            while (curr.ParentEdge != null && curr.Parent != null)
            {
                path.Add(curr.ParentEdge);
                curr = curr.Parent;
            }
            path.Reverse();

            curr = backwardTree[meetingNode];
            while (curr.ParentEdge != null && curr.Parent != null)
            {
                path.Add(curr.ParentEdge);
                curr = curr.Parent;
            }
            return path;
        }

        // Фоллбэк: приоритет функциональным вызовам, отсекая чистые Contains
        var dynamicChain = new List<GraphEdge>();
        var explorer = forwardRoot;
        while (explorer.Children.Count > 0 && dynamicChain.Count < 5)
        {
            var bestChild = explorer.Children
                .OrderByDescending(c => c.ParentEdge?.Relation is RelationType.Calls or RelationType.ExecTrace)
                .ThenByDescending(c => c.Visits)
                .FirstOrDefault();

            if (bestChild?.ParentEdge == null) break;

            dynamicChain.Add(bestChild.ParentEdge);
            explorer = bestChild;
        }

        return dynamicChain;
    }
}