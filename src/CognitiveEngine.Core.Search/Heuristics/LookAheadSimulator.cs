namespace CognitiveEngine.Core.Search.Heuristics;

using CognitiveEngine.Domain.Models;

/// <summary>
/// Симулятор двухшагового предвосхищения тупиковых ветвей графа (Look-Ahead).
/// </summary>
public static class LookAheadSimulator
{
    public static float SimulateBranchViability(
        string candidateNode,
        IReadOnlyList<GraphEdge> localFrontier,
        IReadOnlySet<string> trajectoryHistory)
    {
        // Шаг 1: Потенциальные исходящие пути из кандидата, исключая уже пройденные узлы
        var step1Targets = localFrontier
            .Where(e => e.Source == candidateNode && !trajectoryHistory.Contains(e.Target))
            .Select(e => e.Target)
            .Take(4)
            .ToList();

        if (step1Targets.Count == 0)
        {
            return 0.10f; // Гарантированный тупик прямо за следующим шагом
        }

        // Шаг 2: Моделирование ветвления из найденных узлов
        int step2Paths = 0;
        foreach (var t1 in step1Targets)
        {
            step2Paths += localFrontier.Count(e =>
                e.Source == t1 &&
                e.Target != candidateNode &&
                !trajectoryHistory.Contains(e.Target));
        }

        if (step2Paths == 0 && step1Targets.Count < 2)
        {
            return 0.25f; // Высокий риск попадания в изолированный лист/сироту
        }

        return 1.0f; // Ветка жизнеспособна и имеет архитектурную глубину
    }
}