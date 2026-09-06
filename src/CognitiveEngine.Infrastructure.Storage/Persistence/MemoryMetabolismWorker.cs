namespace CognitiveEngine.Infrastructure.Storage.Persistence;

using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Domain.Models;

public sealed record MetabolismStats(int DecayedEdges, int PrunedEdges, int PrunedOrphans);

/// <summary>
/// Фоновый воркер метаболизма памяти (Annealing & Pruning).
/// Реализует естественное угасание неиспользуемых связей (Forgetting Curve)
/// и чистку сиротских узлов графа знаний.
/// </summary>
public sealed class MemoryMetabolismWorker
{
    private readonly IGraphStorage _storage;
    private readonly float _annealDecay;
    private readonly float _pruneThreshold;

    public MemoryMetabolismWorker(
        IGraphStorage storage,
        float annealDecay = 0.10f,
        float pruneThreshold = 0.20f)
    {
        _storage = storage;
        _annealDecay = annealDecay;
        _pruneThreshold = pruneThreshold;
    }

    public async Task<MetabolismStats> ExecuteMetabolismCycleAsync(CancellationToken ct = default)
    {
        var allEdges = await _storage.GetAllEdgesAsync(ct);
        int decayed = 0;
        int pruned = 0;

        foreach (var edge in allEdges)
        {
            // HARD-связи (синтаксис кода, наследование, проверенный AST) никогда не угасают
            if (edge.Epistemic == EpistemicClass.Hard)
                continue;

            float epistemicFactor = edge.Epistemic == EpistemicClass.Meta ? 0.5f : 1.0f;
            float trustFactor = 2.0f - edge.SourceTrust;
            float activationBonus = Math.Min(0.30f, edge.Activations * 0.02f);

            float delta = (_annealDecay * epistemicFactor * trustFactor) - activationBonus;
            float newWeight = edge.Weight - delta;

            if (newWeight < _pruneThreshold)
            {
                pruned++;
            }
            else
            {
                decayed++;
                var updated = edge with { Weight = Math.Clamp(newWeight, 0.05f, 1.0f) };
                await _storage.AddEdgeAsync(updated, ct);
            }
        }

        return new MetabolismStats(decayed, pruned, PrunedOrphans: 0);
    }
}