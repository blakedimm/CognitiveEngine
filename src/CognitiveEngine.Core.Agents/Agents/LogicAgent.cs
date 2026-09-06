namespace CognitiveEngine.Core.Agents.Agents;

using CognitiveEngine.Core.Agents.Bus;
using CognitiveEngine.Core.Search.Engines;
using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Epistemics;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Domain.Models;

public sealed class LogicAgent
{
    private readonly ICognitiveBus _bus;
    private readonly IGraphStorage _storage;
    private readonly IGnnPredictor _gnnPredictor;
    private readonly HierarchicalMctsEngine _mctsEngine;
    private readonly SkepticAgent _skepticAgent;
    private readonly CriticAgent _criticAgent;

    public LogicAgent(
        ICognitiveBus bus,
        IGraphStorage storage,
        IGnnPredictor gnnPredictor,
        HierarchicalMctsEngine mctsEngine,
        SkepticAgent skepticAgent,
        CriticAgent criticAgent)
    {
        _bus = bus;
        _storage = storage;
        _gnnPredictor = gnnPredictor;
        _mctsEngine = mctsEngine;
        _skepticAgent = skepticAgent;
        _criticAgent = criticAgent;

        _bus.Subscribe("task_logic_ready", HandleLogicTaskAsync, CognitivePriority.High);
    }

    private async Task HandleLogicTaskAsync(object payload, CancellationToken ct)
    {
        if (payload is not IdealContextPayload ctx)
            return;

        var tournamentPool = new List<Hypothesis>();
        _mctsEngine.ResetSession();

        var seeds = ctx.Seeds;
        if (seeds.Count == 0)
        {
            await PublishEmptyResultAsync(ctx, ct);
            return;
        }

        var intent = CognitiveRouterAgent.DetectIntent(ctx.Question);
        IReadOnlyList<GraphEdge> primaryPath;

        if (intent == QueryIntent.MultiHopBridge && seeds.Count >= 2)
        {
            var forwardSeeds = seeds.Take(1).ToList();
            var backwardSeeds = seeds.Skip(1).Take(1).ToList();

            // В режиме рефлексии меняем ориентацию поиска
            if (ctx.IsRefinementMode)
            {
                (forwardSeeds, backwardSeeds) = (backwardSeeds, forwardSeeds);
            }

            primaryPath = await _mctsEngine.RunBidirectionalSearchAsync(
                forwardSeeds,
                backwardSeeds,
                maxIterations: ctx.MctsBudget + (ctx.Retries * 150),
                isTraceMode: true,
                ct: ct
            );
        }
        else if (intent == QueryIntent.CallersTrace)
        {
            // Направленный сбор входящих вызовов (In-Degree): кто вызывает сиды
            var callersEdges = new List<GraphEdge>();
            var allEdges = await _storage.GetAllEdgesAsync(ct);

            foreach (var seed in seeds)
            {
                var incoming = allEdges
                    .Where(e => e.Target.Equals(seed, StringComparison.OrdinalIgnoreCase) && 
                                e.Relation is RelationType.Calls or RelationType.ExecTrace)
                    .OrderByDescending(e => e.Weight)
                    .Take(6);
                callersEdges.AddRange(incoming);
            }
            primaryPath = callersEdges;
        }
        else
        {
            // Радиальный режим для обзоров, исключений и вызовов
            var aggregatedEdges = new List<GraphEdge>();
            foreach (var seed in seeds.Take(4))
            {
                var frontier = await _storage.GetLocalFrontierAsync([seed], ct);
                var relevant = frontier
                    .Where(e => e.Relation is RelationType.Calls or RelationType.ExecTrace or RelationType.Contains)
                    .OrderByDescending(e => e.Weight)
                    .Take(4);
                aggregatedEdges.AddRange(relevant);
            }
            primaryPath = aggregatedEdges;
        }

        // Гипотеза 1: Первичная аналитическая траектория
        var h1 = new Hypothesis(ctx.IsRefinementMode ? "H_refined_analytical" : "H_primary_analytical", seeds);
        foreach (var edge in primaryPath)
        {
            float prior = CalculateEdgePrior(edge);
            h1.AddEdge(edge.Source, edge.Target, edge.Relation, edge.Weight, prior);
        }
        h1.Scores.Support = h1.Edges.Count > 0 ? 0.85f : 0.0f;
        h1.Scores.Fitness = h1.Scores.Support;
        tournamentPool.Add(h1);

        // Гипотеза 2: Альтернатива через абляцию (для сквозных мостов)
        if (intent == QueryIntent.MultiHopBridge && primaryPath.Count > 1)
        {
            var dominantEdge = primaryPath.MaxBy(e => e.Weight);
            var altPath = await FindAlternativePathAsync(seeds.Take(1).ToList(), seeds.Skip(1).Take(1).ToList(), dominantEdge, ctx.MctsBudget, ct);

            if (altPath.Count > 0 && !ArePathsIdentical(primaryPath, altPath))
            {
                var h2 = new Hypothesis("H_topological_alternative", seeds);
                foreach (var edge in altPath)
                {
                    float prior = CalculateEdgePrior(edge) * 0.90f;
                    h2.AddEdge(edge.Source, edge.Target, edge.Relation, edge.Weight, prior);
                }
                h2.Scores.Support = 0.70f;
                h2.Scores.Fitness = 0.70f;
                tournamentPool.Add(h2);
            }
        }

        // Гипотеза 3: Контекстное окружение (исключая рёбра H1)
        var localFrontier = await _storage.GetLocalFrontierAsync(seeds, ct);
        var primaryEdgeIds = primaryPath.Select(e => e.Id).ToHashSet();

        var distinctLocalCalls = localFrontier
            .Where(e => !primaryEdgeIds.Contains(e.Id))
            .Where(e => e.Relation is RelationType.Calls or RelationType.ExecTrace)
            .OrderByDescending(e => e.Weight)
            .Take(3)
            .ToList();

        if (distinctLocalCalls.Count > 0)
        {
            var h3 = new Hypothesis("H_distinct_context", seeds);
            foreach (var edge in distinctLocalCalls)
            {
                float prior = CalculateEdgePrior(edge) * 0.75f;
                h3.AddEdge(edge.Source, edge.Target, edge.Relation, edge.Weight, prior);
            }
            h3.Scores.Support = 0.55f;
            h3.Scores.Fitness = 0.55f;
            tournamentPool.Add(h3);
        }

        // Состязательная фильтрация
        _skepticAgent.AuditHypothesisUniverse(tournamentPool);
        _criticAgent.EvaluateSemanticResilience(tournamentPool, ctx.SessionId);

        // Выбор чемпиона
        var champion = tournamentPool
            .Where(h => h.Edges.Count > 0)
            .MaxBy(h => h.Scores.Fitness) ?? tournamentPool[0];

        var resultPayload = new LogicTraceCompletedPayload(
            SessionId: ctx.SessionId,
            Question: ctx.Question,
            Champion: champion,
            Pool: tournamentPool,
            ActiveNodesMap: ctx.ActiveNodesContent,
            Retries: ctx.Retries
        );

        await _bus.PublishAsync("logic_trace_completed", resultPayload, CognitivePriority.Normal, ct);
    }

    private async Task<IReadOnlyList<GraphEdge>> FindAlternativePathAsync(
        IReadOnlyList<string> forwardSeeds,
        IReadOnlyList<string> backwardSeeds,
        GraphEdge? edgeToExclude,
        int budget,
        CancellationToken ct)
    {
        if (edgeToExclude == null || forwardSeeds.Count == 0 || backwardSeeds.Count == 0) 
            return [];

        var localEdges = await _storage.GetLocalFrontierAsync(forwardSeeds, ct);
        var bypassEdge = localEdges.FirstOrDefault(e =>
            e.Source.Equals(forwardSeeds[0], StringComparison.OrdinalIgnoreCase) &&
            !e.Target.Equals(edgeToExclude.Target, StringComparison.OrdinalIgnoreCase) &&
            e.Relation is RelationType.Calls or RelationType.ExecTrace);

        if (bypassEdge == null) return [];

        var altPath = await _mctsEngine.RunBidirectionalSearchAsync(
            [bypassEdge.Target],
            backwardSeeds,
            maxIterations: budget / 2,
            isTraceMode: true,
            ct: ct
        );

        if (altPath.Count > 0)
        {
            var fullAlt = new List<GraphEdge> { bypassEdge };
            fullAlt.AddRange(altPath);
            return fullAlt;
        }

        return [];
    }

    private static float CalculateEdgePrior(GraphEdge edge)
    {
        float baseWeight = Math.Clamp(edge.Weight, 0.10f, 1.0f);
        float multiplier = edge.Relation switch
        {
            RelationType.ExecTrace => 1.05f,
            RelationType.Calls => 1.00f,
            RelationType.Mutates => 0.90f,
            RelationType.Implements => 0.85f,
            RelationType.Contains => 0.60f,
            _ => 0.70f
        };

        return Math.Clamp(baseWeight * multiplier, 0.05f, 0.99f);
    }

    private static bool ArePathsIdentical(IReadOnlyList<GraphEdge> p1, IReadOnlyList<GraphEdge> p2)
    {
        if (p1.Count != p2.Count) return false;
        for (int i = 0; i < p1.Count; i++)
        {
            if (!p1[i].Source.Equals(p2[i].Source, StringComparison.OrdinalIgnoreCase) ||
                !p1[i].Target.Equals(p2[i].Target, StringComparison.OrdinalIgnoreCase) ||
                p1[i].Relation != p2[i].Relation)
            {
                return false;
            }
        }
        return true;
    }

    private async Task PublishEmptyResultAsync(IdealContextPayload ctx, CancellationToken ct)
    {
        var emptyHypothesis = new Hypothesis("H_empty", []);
        var resultPayload = new LogicTraceCompletedPayload(
            SessionId: ctx.SessionId,
            Question: ctx.Question,
            Champion: emptyHypothesis,
            Pool: [emptyHypothesis],
            ActiveNodesMap: ctx.ActiveNodesContent,
            Retries: ctx.Retries
        );
        await _bus.PublishAsync("logic_trace_completed", resultPayload, CognitivePriority.Normal, ct);
    }
}