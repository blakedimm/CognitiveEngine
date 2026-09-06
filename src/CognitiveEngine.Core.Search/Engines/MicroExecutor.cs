namespace CognitiveEngine.Core.Search.Engines;

using CognitiveEngine.Core.Search.Heuristics;
using CognitiveEngine.Core.Search.Tree;
using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Domain.Models;

/// <summary>
/// Исполнитель микро-вызовов: осуществляет раскрытие и скоринг конкретных функциональных ребер.
/// </summary>
public sealed class MicroExecutor
{
    private static readonly Dictionary<RelationType, float> RelationWeights = new()
    {
        [RelationType.ExecTrace] = 2.50f,
        [RelationType.Calls] = 1.20f,
        [RelationType.Implements] = 0.80f,
        [RelationType.Inherits] = 0.70f,
        [RelationType.Defines] = 0.50f,
        [RelationType.Imports] = 0.30f,
        [RelationType.Contains] = 0.10f,
        [RelationType.Associated] = 0.05f
    };

    public static async Task<List<(GraphEdge Edge, float Prior)>> ScoreFrontierAsync(
        MctsNode currentNode,
        IReadOnlyList<GraphEdge> localEdges,
        IGnnPredictor gnnPredictor,
        MentalScarsRegistry scars,
        string targetMacro,
        bool isTraceMode,
        CancellationToken ct = default)
    {
        var candidates = new List<(GraphEdge Edge, float Prior)>();
        string currentId = currentNode.CurrentNodeId;

        foreach (var edge in localEdges)
        {
            string neighbor = edge.Source == currentId ? edge.Target : edge.Source;
            if (currentNode.VisitedNodes.Contains(neighbor))
                continue;

            float relWeight = RelationWeights.GetValueOrDefault(edge.Relation, 0.10f);
            if (isTraceMode && edge.Epistemic == EpistemicClass.Soft)
                relWeight *= 0.05f;

            // Расчет вероятности через GNN
            float gnnProb = gnnPredictor.IsActive
                ? await gnnPredictor.PredictLinkProbabilityAsync(currentId, neighbor, ct)
                : edge.Weight;

            // Штрафы тупиков и накопленных шрамов
            float lookAheadBonus = LookAheadSimulator.SimulateBranchViability(neighbor, localEdges, currentNode.VisitedNodes);
            float scarPenalty = scars.GetPenalty(neighbor);

            // Бонус соответствия запланированному макро-модулю
            float macroBonus = MacroPlanner.ResolveMacroParent(neighbor) == targetMacro ? 0.35f : 0.0f;

            float prior = Math.Clamp(
                (gnnProb * 0.40f) + (relWeight * 0.30f) + (lookAheadBonus * 0.20f) + macroBonus - scarPenalty,
                0.01f,
                1.0f
            );

            candidates.Add((edge, prior));
        }

        candidates.Sort((a, b) => b.Prior.CompareTo(a.Prior));
        return candidates;
    }
}