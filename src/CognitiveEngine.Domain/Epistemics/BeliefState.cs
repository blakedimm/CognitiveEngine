namespace CognitiveEngine.Domain.Epistemics;

using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Models;

/// <summary>
/// Единое пространство изменчивых убеждений рантайма для конкретной сессии.
/// </summary>
public sealed class BeliefState
{
    public string SessionId { get; }
    public Dictionary<string, GraphNode> Nodes { get; } = new();
    public Dictionary<(string Source, string Target, RelationType Relation), GraphEdge> Edges { get; } = new();
    public Dictionary<(string Source, string Target), float> Confidence { get; } = new();
    public HashSet<string> Contradictions { get; } = new();
    public List<string> MutationHistory { get; } = new();

    public BeliefState(string sessionId)
    {
        SessionId = sessionId;
    }

    public void AddHypothesisEdge(GraphEdge edge, float prior)
    {
        var key = (edge.Source, edge.Target, edge.Relation);
        Edges[key] = edge;
        Confidence[(edge.Source, edge.Target)] = prior;
        MutationHistory.Add($"ADD_EDGE:{edge.Source}->{edge.Target}:{edge.Relation}");
    }
}