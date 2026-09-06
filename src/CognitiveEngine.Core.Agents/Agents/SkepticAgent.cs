namespace CognitiveEngine.Core.Agents.Agents;

using CognitiveEngine.Core.Agents.Bus;
using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Epistemics;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Domain.Models;

/// <summary>
/// Состязательный аудитор топологии.
/// Выявляет циклические зависимости, пинг-понг вызовы, проверяет минимальность 
/// цепочки доказательства и изолирует архитектурные аномалии.
/// </summary>
public sealed class SkepticAgent
{
    private readonly ICognitiveBus _bus;

    public SkepticAgent(ICognitiveBus bus)
    {
        _bus = bus;
    }

    public void AuditHypothesisUniverse(IReadOnlyList<Hypothesis> tournamentPool)
    {
        foreach (var hyp in tournamentPool)
        {
            if (hyp.Edges.Count == 0) continue;

            // 1. Поиск циклов N-го порядка с защищённой размоткой стека
            float cyclePenalty = DetectCyclesStrict(hyp);

            // 2. Детекция взаимного пинг-понга (A calls B AND B calls A)
            float pingPongPenalty = DetectPingPongCalls(hyp);

            // 3. Проверка на висячие тупиковые ветви (не участвующие в соединении сидов)
            float danglingPenalty = CheckDanglingBranches(hyp);

            // 4. Проверка иерархических слоёв кодовой базы Python/ML
            float layerFlowScore = EvaluatePythonAstFlow(hyp);

            // 5. Аддитивный расчёт поддержки (без мультипликативного схлопывания)
            float structuralIntegrity = Math.Clamp(1.0f - (cyclePenalty + pingPongPenalty + danglingPenalty), 0.05f, 1.0f);
            float calibratedSupport = (structuralIntegrity * 0.60f) + (layerFlowScore * 0.40f);

            // Скептик корректирует априорную поддержку гипотезы
            hyp.Scores.Support = (hyp.Scores.Support > 0f)
                ? (hyp.Scores.Support * 0.50f) + (calibratedSupport * 0.50f)
                : calibratedSupport;
        }
    }

    private static float DetectCyclesStrict(Hypothesis hyp)
    {
        var adj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ((src, tgt, rel), _) in hyp.Edges)
        {
            // Циклами считаются только потоки управления и вызовов
            if (rel is RelationType.Calls or RelationType.ExecTrace or RelationType.Causes)
            {
                if (!adj.TryGetValue(src, out var list))
                {
                    list = [];
                    adj[src] = list;
                }
                list.Add(tgt);
            }
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        float penalty = 0f;

        foreach (var node in hyp.Nodes)
        {
            if (!visited.Contains(node))
            {
                DfsCycleCheck(node, adj, visited, stack, hyp, ref penalty);
            }
        }

        return Math.Clamp(penalty, 0.0f, 0.80f);
    }

    private static bool DfsCycleCheck(
        string current,
        Dictionary<string, List<string>> adj,
        HashSet<string> visited,
        HashSet<string> stack,
        Hypothesis hyp,
        ref float penalty)
    {
        visited.Add(current);
        stack.Add(current);

        try
        {
            if (adj.TryGetValue(current, out var neighbors))
            {
                foreach (var nxt in neighbors)
                {
                    if (stack.Contains(nxt))
                    {
                        hyp.Conflicts.Add($"CYCLE_DETECTED:{current}->{nxt}");
                        penalty += 0.45f;
                        return true;
                    }

                    if (!visited.Contains(nxt) && DfsCycleCheck(nxt, adj, visited, stack, hyp, ref penalty))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        finally
        {
            // Гарантированное удаление из стека при возврате из рекурсии
            stack.Remove(current);
        }
    }

    private static float DetectPingPongCalls(Hypothesis hyp)
    {
        float penalty = 0f;
        var directCalls = new HashSet<(string, string)>();

        foreach (var ((src, tgt, rel), _) in hyp.Edges)
        {
            if (rel == RelationType.Calls)
            {
                directCalls.Add((src.ToLowerInvariant(), tgt.ToLowerInvariant()));
            }
        }

        foreach (var (src, tgt) in directCalls)
        {
            if (directCalls.Contains((tgt, src)) && !src.Equals(tgt, StringComparison.OrdinalIgnoreCase))
            {
                hyp.Conflicts.Add($"PING_PONG_CALL:{src}<->{tgt}");
                penalty += 0.30f;
            }
        }

        return Math.Clamp(penalty, 0.0f, 0.60f);
    }

    private static float CheckDanglingBranches(Hypothesis hyp)
    {
        if (hyp.Seeds.Count < 2 || hyp.Edges.Count <= 2) return 0f;

        string start = hyp.Seeds[0];
        string target = hyp.Seeds[^1];

        // Проверяем, достижим ли таргет из старта
        var reachableFromStart = GetReachableNodes(start, hyp);
        if (!reachableFromStart.Contains(target))
        {
            hyp.Conflicts.Add($"PATH_DISCONNECTED:{start}_to_{target}");
            return 0.40f;
        }

        // Рёбра, чьи узлы не входят в транзитивный путь между сидами, считаются мусором
        int danglingEdges = 0;
        foreach (var ((src, tgt, _), _) in hyp.Edges)
        {
            if (!reachableFromStart.Contains(src) && !reachableFromStart.Contains(tgt))
            {
                danglingEdges++;
            }
        }

        return danglingEdges > 0 ? Math.Clamp(danglingEdges * 0.10f, 0.0f, 0.30f) : 0f;
    }

    private static HashSet<string> GetReachableNodes(string startNode, Hypothesis hyp)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        queue.Enqueue(startNode);
        visited.Add(startNode);

        while (queue.Count > 0)
        {
            var curr = queue.Dequeue();
            foreach (var ((src, tgt, _), _) in hyp.Edges)
            {
                if (src.Equals(curr, StringComparison.OrdinalIgnoreCase) && visited.Add(tgt))
                {
                    queue.Enqueue(tgt);
                }
            }
        }

        return visited;
    }

    private static float EvaluatePythonAstFlow(Hypothesis hyp)
    {
        float score = 1.0f;

        foreach (var ((src, tgt, rel), _) in hyp.Edges)
        {
            int srcLevel = GetAstLevel(src);
            int tgtLevel = GetAstLevel(tgt);

            // Contains может идти только сверху вниз (Module -> Class -> Function)
            if (rel == RelationType.Contains && srcLevel >= tgtLevel && srcLevel != 0 && tgtLevel != 0)
            {
                hyp.Conflicts.Add($"ILLEGAL_CONTAINS_HIERARCHY:{src}->{tgt}");
                score -= 0.15f;
            }

            // Модули не могут вызывать функции напрямую без точки входа
            if (rel == RelationType.Calls && src.StartsWith("mod_", StringComparison.OrdinalIgnoreCase))
            {
                hyp.Conflicts.Add($"MODULE_DIRECT_CALL:{src}->{tgt}");
                score -= 0.10f;
            }
        }

        return Math.Clamp(score, 0.20f, 1.0f);
    }

    private static int GetAstLevel(string nodeId)
    {
        if (nodeId.StartsWith("mod_", StringComparison.OrdinalIgnoreCase)) return 1;
        if (nodeId.StartsWith("cls_", StringComparison.OrdinalIgnoreCase)) return 2;
        if (nodeId.StartsWith("func_", StringComparison.OrdinalIgnoreCase)) return 3;
        return 0;
    }
}