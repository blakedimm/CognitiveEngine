namespace CognitiveEngine.Domain.Epistemics;

using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Models;

/// <summary>
/// Оценки жизнеспособности и топологического качества гипотезы.
/// </summary>
public sealed class HypothesisScores
{
    public float Support { get; set; } = 0.5f;
    public float Contradiction { get; set; } = 0.0f;
    public float Complexity { get; set; } = 0.0f;
    public float BridgeValue { get; set; } = 0.0f;
    public float Fitness { get; set; } = 0.5f;
}

/// <summary>
/// Метаданные ребра внутри исследуемой гипотезы.
/// </summary>
public readonly record struct HypothesisEdgeMeta(float Weight, float Prior);

/// <summary>
/// Индивидуальная топологическая гипотеза (подграф рассуждения в турнире).
/// </summary>
public sealed class Hypothesis
{
    public string Id { get; }
    public IReadOnlyList<string> Seeds { get; }
    public Dictionary<(string Source, string Target, RelationType Relation), HypothesisEdgeMeta> Edges { get; } = new();
    public HashSet<string> Nodes { get; } = new();
    public HashSet<string> Conflicts { get; } = new();
    public HypothesisScores Scores { get; } = new();

    public Hypothesis(string id, IEnumerable<string> seeds)
    {
        Id = id;
        Seeds = seeds.ToList();
        foreach (var seed in Seeds)
        {
            Nodes.Add(seed);
        }
    }

    public void AddEdge(string source, string target, RelationType relation, float weight, float prior)
    {
        Edges[(source, target, relation)] = new HypothesisEdgeMeta(weight, prior);
        Nodes.Add(source);
        Nodes.Add(target);
    }
}