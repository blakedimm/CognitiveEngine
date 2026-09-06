namespace CognitiveEngine.Core.Agents.Agents;

using System.Collections.Concurrent;
using CognitiveEngine.Core.Agents.Bus;
using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Epistemics;
using CognitiveEngine.Domain.Interfaces;

/// <summary>
/// Семантический цензор (Critic Agent).
/// Проверяет топологическую непрерывность маршрута, фильтрует паразитные Contains-структуры,
/// оценивает семантическую динамику вызовов и предотвращает зацикливание.
/// </summary>
public sealed class CriticAgent
{
    private readonly ICognitiveBus _bus;
    private readonly ConcurrentDictionary<string, List<float>> _sessionFitnessHistory = new();
    private const int HistoryWindowSize = 4;

    private static readonly Dictionary<RelationType, float> RelationDynamicWeights = new()
    {
        [RelationType.ExecTrace] = 1.5f,
        [RelationType.Calls] = 1.4f,
        [RelationType.Mutates] = 1.2f,
        [RelationType.Implements] = 1.0f,
        [RelationType.Inherits] = 0.9f,
        [RelationType.Imports] = 0.5f,
        [RelationType.Contains] = 0.2f, // Структурный туннель, минимальный вклад в причинность
        [RelationType.Associated] = 0.3f
    };

    public CriticAgent(ICognitiveBus bus)
    {
        _bus = bus;
    }

    public void ResetSession()
    {
        _sessionFitnessHistory.Clear();
    }

    public void EvaluateSemanticResilience(IReadOnlyList<Hypothesis> tournamentPool, string? sessionId = null)
    {
        foreach (var hyp in tournamentPool)
        {
            if (hyp.Edges.Count == 0)
            {
                hyp.Scores.Fitness = 0.0f;
                continue;
            }

            // 1. Проверка топологической связности цепи (отсутствие разрывов)
            float continuityScore = EvaluatePathContinuity(hyp);

            // 2. Семантическая динамика (Calls/ExecTrace против чистых Contains-туннелей)
            float flowRatio = CalculateDynamicFlowRatio(hyp);

            // 3. Статистическая уверенность (длина-нормализованная)
            float confidence = CalculateNormalizedConfidence(hyp);

            // 4. Архитектурные инверсии и антипаттерны
            float structuralPenalty = CheckStructuralInversions(hyp);

            // Итоговый баланс фитнеса
            float rawFitness = (confidence * 0.35f) + 
                               (continuityScore * 0.30f) + 
                               (flowRatio * 0.25f) + 
                               (hyp.Scores.Support * 0.10f) - 
                               structuralPenalty;

            float finalFitness = Math.Clamp(rawFitness, 0.01f, 1.0f);
            hyp.Scores.Fitness = finalFitness;

            // Детекция зацикливания в рамках сессии
            string trackingKey = string.IsNullOrEmpty(sessionId) ? hyp.Id : $"{sessionId}_{hyp.Id}";
            var history = _sessionFitnessHistory.GetOrAdd(trackingKey, _ => new List<float>());

            lock (history)
            {
                history.Add(finalFitness);
                if (history.Count > HistoryWindowSize)
                {
                    history.RemoveAt(0);
                }

                if (DetectLimitCycle(history))
                {
                    hyp.Conflicts.Add("EPISTEMIC_SHOCK:limit_cycle_detected");
                    hyp.Scores.Fitness = Math.Max(0.01f, finalFitness - 0.25f);
                }
            }
        }
    }

    /// <summary>
    /// Проверяет, образуют ли ребра связанную цепочку, а не разрозненный набор фактов.
    /// </summary>
    private static float EvaluatePathContinuity(Hypothesis hyp)
    {
        if (hyp.Edges.Count <= 1) return 1.0f;

        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var inDegrees = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var ((src, tgt, _), _) in hyp.Edges)
        {
            if (!adjacency.TryGetValue(src, out var list))
            {
                list = [];
                adjacency[src] = list;
            }
            list.Add(tgt);

            inDegrees[tgt] = inDegrees.GetValueOrDefault(tgt) + 1;
            inDegrees.TryAdd(src, 0);
        }

        // Число корневых вершин (входных точек без входящих ребер)
        int rootCount = inDegrees.Count(kv => kv.Value == 0);

        // Идеальный путь имеет ровно 1 начало. 2+ начала означают разорванные куски графа.
        return rootCount switch
        {
            1 => 1.0f,
            2 => 0.65f,
            _ => 0.30f
        };
    }

    /// <summary>
    /// Штрафует цепочки, состоящие исключительно из синтаксических Contains-отношений.
    /// </summary>
    private static float CalculateDynamicFlowRatio(Hypothesis hyp)
    {
        float totalDynamicScore = 0f;

        foreach (var (key, _) in hyp.Edges)
        {
            totalDynamicScore += RelationDynamicWeights.GetValueOrDefault(key.Relation, 0.5f);
        }

        float avgWeight = totalDynamicScore / hyp.Edges.Count;

        // Нормализуем относительно эталонного диапазона [0.2 (чистый Contains) .. 1.5 (чистый ExecTrace)]
        return Math.Clamp((avgWeight - 0.2f) / 1.3f, 0.1f, 1.0f);
    }

    /// <summary>
    /// Геометрическая уверенность с затуханием дисперсии, не штрафующая за длину маршрута.
    /// </summary>
    private static float CalculateNormalizedConfidence(Hypothesis hyp)
    {
        var priors = hyp.Edges.Values.Select(e => Math.Clamp(e.Prior, 0.05f, 1.0f)).ToList();
        if (priors.Count == 0) return 0.0f;

        double logSum = priors.Sum(p => Math.Log(p));
        double geoMean = Math.Exp(logSum / priors.Count);

        // Мягкий штраф за разброс уверенности (стабильность звеньев)
        float minPrior = priors.Min();
        float stabilityFactor = 0.75f + 0.25f * minPrior;

        return (float)(geoMean * stabilityFactor);
    }

    /// <summary>
    /// Проверка архитектурных инверсий: функции не могут владеть классами, а внутренние хелперы — модулями.
    /// </summary>
    private static float CheckStructuralInversions(Hypothesis hyp)
    {
        float penalty = 0.0f;

        foreach (var ((src, tgt, rel), _) in hyp.Edges)
        {
            bool srcIsFunc = src.StartsWith("func_", StringComparison.OrdinalIgnoreCase);
            bool tgtIsClass = tgt.StartsWith("cls_", StringComparison.OrdinalIgnoreCase);
            bool tgtIsMod = tgt.StartsWith("mod_", StringComparison.OrdinalIgnoreCase);

            // Инверсия иерархии: функция объявляет/содержит класс или модуль
            if (srcIsFunc && rel == RelationType.Contains && (tgtIsClass || tgtIsMod))
            {
                hyp.Conflicts.Add($"ARCH_INVERSION:{src}_contains_{tgt}");
                penalty += 0.35f;
            }

            // Циклическая зависимость вызова самого себя без терминального условия
            if (src.Equals(tgt, StringComparison.OrdinalIgnoreCase) && rel == RelationType.Calls)
            {
                hyp.Conflicts.Add($"RECURSION_RISK:{src}");
                penalty += 0.20f;
            }
        }

        return Math.Clamp(penalty, 0.0f, 0.8f);
    }

    private static bool DetectLimitCycle(List<float> history)
    {
        if (history.Count < HistoryWindowSize) return false;

        float mean = history.Average();
        double variance = history.Sum(val => Math.Pow(val - mean, 2)) / history.Count;

        float deltaSum = 0.0f;
        for (int i = 1; i < history.Count; i++)
        {
            deltaSum += Math.Abs(history[i] - history[i - 1]);
        }

        float avgDelta = deltaSum / (history.Count - 1);
        return avgDelta > 0.005f && variance < 0.0008;
    }
}