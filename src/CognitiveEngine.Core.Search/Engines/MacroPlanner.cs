namespace CognitiveEngine.Core.Search.Engines;

using CognitiveEngine.Domain.Models;

/// <summary>
/// Планировщик макро-траекторий между архитектурными подсистемами и модулями.
/// </summary>
public sealed class MacroPlanner
{
    private static readonly string[] KnownSubsystems =
    [
        "ingest", "database", "main", "pipeline", "agents",
        "model", "optimizer", "mcts", "handlers", "redis", "storage"
    ];

    public static string ResolveMacroParent(string nodeId)
    {
        if (nodeId.StartsWith("mod_")) return nodeId;

        ReadOnlySpan<char> span = nodeId.AsSpan();
        foreach (var sub in KnownSubsystems)
        {
            if (span.Contains(sub.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return $"mod_{sub}";
        }

        return "mod_main";
    }

    public List<string> PlanMacroRoute(string startNodeId, string targetNodeId, IReadOnlyList<GraphEdge> allEdges)
    {
        string startMacro = ResolveMacroParent(startNodeId);
        string targetMacro = ResolveMacroParent(targetNodeId);

        if (startMacro == targetMacro)
            return [startMacro];

        var macroGraph = new Dictionary<string, HashSet<string>>();
        foreach (var e in allEdges)
        {
            string sMac = ResolveMacroParent(e.Source);
            string tMac = ResolveMacroParent(e.Target);
            if (sMac == tMac) continue;

            if (!macroGraph.TryGetValue(sMac, out var neighbors))
            {
                neighbors = new HashSet<string>();
                macroGraph[sMac] = neighbors;
            }
            neighbors.Add(tMac);
        }

        // BFS поиск кратчайшего пути на уровне макро-модулей
        var queue = new Queue<List<string>>();
        var visited = new HashSet<string> { startMacro };
        queue.Enqueue([startMacro]);

        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            string current = path[^1];

            if (current == targetMacro)
                return path;

            if (macroGraph.TryGetValue(current, out var nextSet))
            {
                foreach (var nxt in nextSet)
                {
                    if (visited.Add(nxt))
                    {
                        var newPath = new List<string>(path.Count + 1);
                        newPath.AddRange(path);
                        newPath.Add(nxt);
                        queue.Enqueue(newPath);
                    }
                }
            }
        }

        return [startMacro, targetMacro];
    }
}