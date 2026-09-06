namespace CognitiveEngine.Core.Search.Heuristics;

using CognitiveEngine.Core.Search.Tree;

/// <summary>
/// Калькулятор формулы PUCT (Polynomial Upper Confidence Trees) с динамической регуляризацией.
/// </summary>
public static class PuctCalculator
{
    public static double ComputeScore(MctsNode child, double parentLogTotal, double cPuct, double dynamicBoost = 1.0)
    {
        double uScore = child.Value + (cPuct * dynamicBoost * child.Prior * (parentLogTotal / (1.0 + child.Visits)));
        return uScore;
    }

    /// <summary>
    /// Рекурсивно спускается по дереву MCTS, выбирая лучших детей по формуле PUCT,
    /// пока не достигнет нераскрытого узла или листа.
    /// </summary>
    public static MctsNode SelectPromisingNode(MctsNode root, double cPuct, double dynamicBoost = 1.0)
    {
        var current = root;

        while (current.Children.Count > 0)
        {
            // Если есть неисследованные дети — берем первого неисследованного
            for (int i = 0; i < current.Children.Count; i++)
            {
                if (current.Children[i].Visits == 0)
                    return current.Children[i];
            }

            double logTotal = Math.Sqrt(Math.Max(1, current.Visits));
            double bestScore = double.NegativeInfinity;
            MctsNode? bestChild = null;

            for (int i = 0; i < current.Children.Count; i++)
            {
                var child = current.Children[i];
                if (child.IsExpanded && child.Children.Count == 0) continue;

                double score = ComputeScore(child, logTotal, cPuct, dynamicBoost);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestChild = child;
                }
            }

            if (bestChild == null) break;
            current = bestChild;
        }

        return current;
    }
}