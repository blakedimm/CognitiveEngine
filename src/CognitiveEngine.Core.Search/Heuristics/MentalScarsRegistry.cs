namespace CognitiveEngine.Core.Search.Heuristics;

using System.Collections.Concurrent;

/// <summary>
/// Реестр кратковременной памяти ошибок и тупиков («ментальных шрамов») сессии поиска.
/// </summary>
public sealed class MentalScarsRegistry
{
    private readonly ConcurrentDictionary<string, float> _scars = new();

    public void AddScar(string nodeId, float penalty = 1.5f)
    {
        _scars.AddOrUpdate(nodeId, penalty, (_, existing) => existing + penalty);
    }

    public float GetPenalty(string nodeId)
    {
        return _scars.TryGetValue(nodeId, out var penalty) ? penalty : 0.0f;
    }

    public void Reset()
    {
        _scars.Clear();
    }
}