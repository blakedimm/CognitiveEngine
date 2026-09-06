namespace CognitiveEngine.Core.Agents.Agents;

using CognitiveEngine.Core.Agents.Bus;
using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Epistemics;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Domain.Models;

public sealed record VerifiedClaim(
    string Source,
    string Target,
    RelationType Relation,
    float GraphWeight,
    float GnnPrior,
    float PolicyImportance,
    float FusedScore,
    bool GroundTruthExists = true
);

/// <summary>
/// Эпистемический валидатор рантайма (Reality & Fact-Checking Gate).
/// Сверяет гипотезы с физической топологией AST, рассчитывает FSR по принципу слабого звена,
/// выявляет разрывы и инициирует рекуррентное додумывание System 2.
/// </summary>
public sealed class FactCheckerAgent
{
    private readonly ICognitiveBus _bus;
    private readonly IGraphStorage _storage;

    private static readonly Dictionary<RelationType, float> RelationImportance = new()
    {
        [RelationType.ExecTrace] = 1.00f,
        [RelationType.Calls] = 0.95f,
        [RelationType.Mutates] = 0.85f,
        [RelationType.Implements] = 0.90f,
        [RelationType.Inherits] = 0.90f,
        [RelationType.Defines] = 0.80f,
        [RelationType.Imports] = 0.60f,
        [RelationType.Contains] = 0.40f,
        [RelationType.Associated] = 0.30f
    };

    public FactCheckerAgent(ICognitiveBus bus, IGraphStorage storage)
    {
        _bus = bus;
        _storage = storage;

        _bus.Subscribe("logic_trace_completed", VerifyFactsAsync, CognitivePriority.High);
    }

    private async Task VerifyFactsAsync(object payload, CancellationToken ct)
    {
        if (payload is not LogicTraceCompletedPayload data)
            return;

        string sessionId = data.SessionId;
        string question = data.Question;
        Hypothesis champion = data.Champion;
        IReadOnlyList<Hypothesis> pool = data.Pool;
        IReadOnlyDictionary<string, string> activeNodesMap = data.ActiveNodesMap;
        int retries = data.Retries;

        int edgeCount = champion.Edges.Count;

        var verifiedClaims = new List<VerifiedClaim>(edgeCount);
        float sumFused = 0f;
        float minFused = edgeCount > 0 ? 1.0f : 0.0f;
        int ungroundedEdgesCount = 0;

        foreach (var ((src, tgt, rel), meta) in champion.Edges)
        {
            // Физическая проверка в хранилище через O(1) индекс
            var physicalEdge = await _storage.FindEdgeAsync(src, tgt, ct);
            bool existsInStorage = physicalEdge != null;
            if (!existsInStorage) ungroundedEdgesCount++;

            float actualGraphWeight = existsInStorage ? physicalEdge!.Weight : 0.0f;
            float pGnn = Math.Clamp(meta.Prior, 0.01f, 1.0f);
            float pPolicy = RelationImportance.GetValueOrDefault(rel, 0.50f);

            // Если связи нет в графе AST, она строго наказывается (GNN не спасает фейк)
            float fused = existsInStorage
                ? (actualGraphWeight * 0.40f) + (pGnn * 0.40f) + (pPolicy * 0.20f)
                : 0.01f;

            sumFused += fused;
            if (fused < minFused) minFused = fused;

            verifiedClaims.Add(new VerifiedClaim(
                Source: src,
                Target: tgt,
                Relation: rel,
                GraphWeight: actualGraphWeight,
                GnnPrior: pGnn,
                PolicyImportance: pPolicy,
                FusedScore: fused,
                GroundTruthExists: existsInStorage
            ));
        }

        float avgFused = edgeCount > 0 ? (sumFused / edgeCount) : 0f;
        
        // FSR: 60% средняя прочность + 40% принцип слабого звена (Bottleneck)
        float fsrBase = (avgFused * 0.60f) + (minFused * 0.40f);

        float conflictRatio = (float)champion.Conflicts.Count / Math.Max(1, edgeCount);
        float conflictDamping = Math.Clamp(1.0f - (conflictRatio * 0.40f), 0.10f, 1.0f);

        // Строгое подавление FSR при наличии неподтверждённых рёбер
        float ungroundedRatio = edgeCount > 0 ? ((float)ungroundedEdgesCount / edgeCount) : 0f;
        float groundTruthDamping = Math.Clamp(1.0f - (ungroundedRatio * 0.85f), 0.02f, 1.0f);

        float finalFsr = Math.Clamp(fsrBase * conflictDamping * groundTruthDamping, 0.0f, 1.0f);

        // Дедупликация гипотез и расчёт энтропии
        var uniqueHypotheses = new List<Hypothesis>();
        var seenSignatures = new HashSet<string>();

        foreach (var h in pool.Where(h => h.Edges.Count > 0 && h.Scores.Fitness > 0.05f))
        {
            string signature = string.Join("|", h.Edges.Keys.OrderBy(k => k.Source).ThenBy(k => k.Target).Select(k => $"{k.Source}->{k.Relation}->{k.Target}"));
            if (seenSignatures.Add(signature))
            {
                uniqueHypotheses.Add(h);
            }
        }

        double shannonEntropy = 0.0;
        float entropyCollapse;

        if (uniqueHypotheses.Count <= 1)
        {
            shannonEntropy = 0.0;
            entropyCollapse = uniqueHypotheses.Count == 1 ? 0.50f : 0.0f;
        }
        else
        {
            float totalFitness = uniqueHypotheses.Sum(h => h.Scores.Fitness);
            foreach (var hyp in uniqueHypotheses)
            {
                double p = hyp.Scores.Fitness / (totalFitness > 0 ? totalFitness : 1.0f);
                if (p > 0.0001) shannonEntropy -= p * Math.Log2(p);
            }

            double maxEntropy = Math.Log2(uniqueHypotheses.Count);
            float normalizedEntropy = maxEntropy > 0 ? (float)(shannonEntropy / maxEntropy) : 0.0f;
            entropyCollapse = Math.Clamp(1.0f - normalizedEntropy, 0.0f, 1.0f);
        }

        // =========================================================================
        // 🧠 РЕКУРРЕНТНЫЙ ЗАМОК (Cognitive Dissonance / System 2 Loop)
        // =========================================================================
        bool isZeroEdgesFailure = edgeCount == 0;
        bool isBrokenChain = edgeCount > 1 && (minFused < 0.20f || ungroundedEdgesCount > 0);
        bool hasFatalCycles = champion.Conflicts.Any(c => c.StartsWith("CYCLE_DETECTED"));

        if ((isZeroEdgesFailure || isBrokenChain || hasFatalCycles) && retries == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"    -> [System 2 Loop] Диссонанс (Рёбер: {edgeCount}, Неподтверждено: {ungroundedEdgesCount}, FSR: {finalFsr * 100:F1}%). Запуск адаптивной мутации...");
            Console.ResetColor();

            var recoveryFrontier = activeNodesMap.Count > 0
                ? await _storage.GetLocalFrontierAsync(activeNodesMap.Keys.Take(8), ct)
                : [];

            var retryPayload = new IdealContextPayload(
                SessionId: sessionId,
                Question: question,
                Domain: _storage.Domain,
                Mode: ExecutionPlane.System2_Deliberation,
                MctsBudget: 500,
                Seeds: champion.Seeds,
                SubgraphEdges: recoveryFrontier,
                NodePressures: new Dictionary<string, float>(),
                ActiveNodesContent: activeNodesMap,
                Retries: retries + 1,
                IsRefinementMode: true
            );

            await _bus.PublishAsync("task_logic_ready", retryPayload, CognitivePriority.Critical, ct);
            return;
        }

        // Подкрепляем только подтверждённые вызовы при высоком пороге FSR
        if (finalFsr >= 0.85f && !hasFatalCycles && ungroundedEdgesCount == 0)
        {
            foreach (var claim in verifiedClaims.Where(c => c.GroundTruthExists && c.Relation is RelationType.Calls or RelationType.ExecTrace))
            {
                await _storage.ReinforceClaimAsync(claim.Source, claim.Target, claim.Relation, ct);
            }
        }

        var metrics = new EpistemicMetrics(
            finalFsr,
            (float)shannonEntropy,
            entropyCollapse,
            Math.Clamp(1.0f - conflictRatio, 0.0f, 1.0f)
        );

        var verifiedPayload = new FactsVerifiedPayload(
            SessionId: sessionId,
            Question: question,
            Champion: champion,
            Claims: verifiedClaims,
            Metrics: metrics,
            ActiveNodesMap: activeNodesMap,
            Status: (finalFsr >= 0.60f && ungroundedEdgesCount == 0) ? "success" : "low_confidence"
        );

        await _bus.PublishAsync("facts_verified", verifiedPayload, CognitivePriority.Normal, ct);
    }
}