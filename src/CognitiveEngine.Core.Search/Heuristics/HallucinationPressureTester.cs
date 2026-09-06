namespace CognitiveEngine.Core.Search.Heuristics;

using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Models;

/// <summary>
/// Стресс-тест физической валидности построенной траектории вызовов по памяти RAM.
/// </summary>
public static class HallucinationPressureTester
{
    public static IReadOnlyList<string> VerifyTrajectory(
        IReadOnlyList<string> trajectory,
        IReadOnlyList<GraphEdge> physicalEdges,
        MentalScarsRegistry scarsRegistry)
    {
        if (trajectory.Count <= 1)
            return trajectory;

        var edgeSet = new HashSet<(string Source, string Target)>(physicalEdges.Count);
        foreach (var e in physicalEdges)
        {
            edgeSet.Add((e.Source, e.Target));
            // Иерархические связи контейнеризации валидируются в обе стороны
            if (e.Relation == RelationType.Contains)
            {
                edgeSet.Add((e.Target, e.Source));
            }
        }

        var verified = new List<string>(trajectory.Count) { trajectory[0] };

        for (int i = 0; i < trajectory.Count - 1; i++)
        {
            string src = trajectory[i];
            string tgt = trajectory[i + 1];

            if (edgeSet.Contains((src, tgt)))
            {
                verified.Add(tgt);
            }
            else
            {
                // Фиксация ментального шрама на узле, где произошел разрыв реального Call Graph
                scarsRegistry.AddScar(tgt, penalty: 2.0f);
                break;
            }
        }

        return verified;
    }
}