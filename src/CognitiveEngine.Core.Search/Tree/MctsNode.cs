namespace CognitiveEngine.Core.Search.Tree;

using CognitiveEngine.Domain.Models;

/// <summary>
/// Узел дерева поиска MCTS. Хранит статистику посещений, априорную вероятность,
/// историю пройденных узлов (VisitedNodes) и обратную связь графа.
/// </summary>
public sealed class MctsNode
{
    public string Id { get; }
    public string NodeId => Id;
    public string CurrentNodeId => Id;
    public string State => Id;

    public bool IsForward { get; }
    public MctsNode? Parent { get; }
    public GraphEdge? ParentEdge { get; }
    public List<MctsNode> Children { get; } = new();
    public HashSet<string> VisitedNodes { get; }

    public int Visits { get; set; }
    public double TotalReward { get; set; }
    public double TotalScore
    {
        get => TotalReward;
        set => TotalReward = value;
    }

    public double Value => Visits > 0 ? TotalReward / Visits : 0.0;
    public float PriorProbability { get; set; }
    public float Prior
    {
        get => PriorProbability;
        set => PriorProbability = value;
    }

    public bool IsExpanded { get; set; }

    public MctsNode(
        string id,
        bool isForward = true,
        MctsNode? parent = null,
        GraphEdge? parentEdge = null,
        float prior = 1.0f)
    {
        Id = id;
        IsForward = isForward;
        Parent = parent;
        ParentEdge = parentEdge;
        PriorProbability = prior;

        VisitedNodes = parent != null
            ? new HashSet<string>(parent.VisitedNodes)
            : new HashSet<string>();
        VisitedNodes.Add(id);
    }

    public MctsNode AddChild(string childId, GraphEdge edge, float prior = 1.0f)
    {
        var child = new MctsNode(childId, IsForward, this, edge, prior);
        Children.Add(child);
        return child;
    }

    public void Backpropagate(double reward)
    {
        var curr = this;
        while (curr != null)
        {
            curr.Visits++;
            curr.TotalReward += reward;
            curr = curr.Parent;
        }
    }
}