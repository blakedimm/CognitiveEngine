namespace CognitiveEngine.Runtime;

using System.Collections.Concurrent;
using CognitiveEngine.Domain.Epistemics;

public sealed record TelemetrySnapshot(
    string SessionId,
    float Fsr,
    float EntropyCollapse,
    long LatencyMs,
    int TotalNodesActivated,
    int TotalEdgesTraversed,
    float CompressionRatio,
    DateTimeOffset Timestamp
);

/// <summary>
/// Диспетчер телеметрии и сбора метрик производительности когнитивного рантайма.
/// Отслеживает задержки инференса, скользящее среднее FSR, коллапс энтропии и стабильность доказательств.
/// </summary>
public sealed class TelemetryTracker
{
    private readonly ConcurrentQueue<TelemetrySnapshot> _history = new();
    private const int MaxHistorySize = 100;

    public void RecordSession(
        string sessionId,
        EpistemicMetrics metrics,
        long latencyMs,
        int nodesActivated,
        int edgesTraversed,
        int rawPromptTokens,
        int condensedProofTokens)
    {
        float compressionRatio = rawPromptTokens > 0
            ? (float)condensedProofTokens / rawPromptTokens
            : 1.0f;

        var snapshot = new TelemetrySnapshot(
            SessionId: sessionId,
            Fsr: metrics.Fsr,
            EntropyCollapse: metrics.EntropyCollapse,
            LatencyMs: latencyMs,
            TotalNodesActivated: nodesActivated,
            TotalEdgesTraversed: edgesTraversed,
            CompressionRatio: compressionRatio,
            Timestamp: DateTimeOffset.UtcNow
        );

        _history.Enqueue(snapshot);

        while (_history.Count > MaxHistorySize)
        {
            _history.TryDequeue(out _);
        }
    }

    public (float AvgFsr, float AvgEntropyCollapse, double AvgLatencyMs) GetAggregateMetrics()
    {
        var snapshots = _history.ToArray();
        if (snapshots.Length == 0)
            return (0f, 0f, 0.0);

        float avgFsr = snapshots.Average(s => s.Fsr);
        float avgEntropy = snapshots.Average(s => s.EntropyCollapse);
        double avgLatency = snapshots.Average(s => s.LatencyMs);

        return (avgFsr, avgEntropy, avgLatency);
    }
}