namespace CognitiveEngine.Core.Search.Heuristics;

/// <summary>
/// Движок анализа структурной энтропии переключения контекстов в траектории вызовов.
/// </summary>
public static class PathEntropyEngine
{
    public static float CalculateEntropy(IReadOnlyList<string> trajectory, Func<string, string> macroParentSelector)
    {
        if (trajectory.Count <= 1)
            return 0.0f;

        int switches = 0;
        string prevMacro = macroParentSelector(trajectory[0]);

        for (int i = 1; i < trajectory.Count; i++)
        {
            string currentMacro = macroParentSelector(trajectory[i]);
            if (currentMacro != prevMacro)
            {
                switches++;
                prevMacro = currentMacro;
            }
        }

        float switchRate = (float)switches / (trajectory.Count - 1);
        float uniqueNodesRatio = (float)trajectory.Distinct().Count() / trajectory.Count;
        float diversityPenalty = 1.0f - uniqueNodesRatio;

        return Math.Clamp((switchRate * 0.6f) + (diversityPenalty * 0.4f), 0.0f, 1.0f);
    }
}