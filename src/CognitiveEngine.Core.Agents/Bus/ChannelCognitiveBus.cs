namespace CognitiveEngine.Core.Agents.Bus;

using System.Collections.Concurrent;
using System.Threading.Channels;
using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Epistemics;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Domain.Models;

public sealed record RouteTaskPayload(string Question, string SessionId);

/// <summary>
/// Контекст задачи, сформированный и проверенный префронтальным координатором.
/// Вынесен в слой шины для сквозного доступа всеми агентами контура ECS.
/// </summary>
public sealed record IdealContextPayload(
    string SessionId,
    string Question,
    string Domain,
    ExecutionPlane Mode,
    int MctsBudget,
    IReadOnlyList<string> Seeds,
    IReadOnlyList<GraphEdge> SubgraphEdges,
    IReadOnlyDictionary<string, float> NodePressures,
    IReadOnlyDictionary<string, string> ActiveNodesContent,
    int Retries = 0,
    bool IsRefinementMode = false
);

public sealed record LogicTraceCompletedPayload(
    string SessionId,
    string Question,
    Hypothesis Champion,
    IReadOnlyList<Hypothesis> Pool,
    IReadOnlyDictionary<string, string> ActiveNodesMap,
    int Retries
);

public sealed record FactsVerifiedPayload(
    string SessionId,
    string Question,
    Hypothesis Champion,
    IReadOnlyList<Agents.VerifiedClaim> Claims,
    EpistemicMetrics Metrics,
    IReadOnlyDictionary<string, string> ActiveNodesMap,
    string Status
);

/// <summary>
/// Адаптивная шина координации рантайма ECS на базе Channels .NET 10 с неблокирующей семафорной сигнализацией.
/// </summary>
public sealed class ChannelCognitiveBus : ICognitiveBus, IAsyncDisposable
{
    private readonly Channel<CognitiveEnvelope> _criticalChannel = Channel.CreateUnbounded<CognitiveEnvelope>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<CognitiveEnvelope> _highChannel = Channel.CreateUnbounded<CognitiveEnvelope>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<CognitiveEnvelope> _normalChannel = Channel.CreateUnbounded<CognitiveEnvelope>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly SemaphoreSlim _signal = new(0);
    private readonly ConcurrentDictionary<string, List<(CognitivePriority Priority, Func<object, CancellationToken, Task> Handler)>> _subscribers = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _dispatcherTask;

    public DeadLetterQueue DeadLetters { get; } = new();
    public float GlobalEntropy { get; private set; } = 0.0f;

    public ChannelCognitiveBus()
    {
        _dispatcherTask = Task.Run(DispatchLoopAsync);
    }

    public void Subscribe(string topic, Func<object, CancellationToken, Task> handler, CognitivePriority priority = CognitivePriority.Normal)
    {
        _subscribers.AddOrUpdate(
            topic,
            _ => [(priority, handler)],
            (_, list) =>
            {
                lock (list)
                {
                    list.Add((priority, handler));
                    list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                }
                return list;
            });
    }

    public async ValueTask PublishAsync(
        string topic,
        object payload,
        CognitivePriority priority = CognitivePriority.Normal,
        CancellationToken ct = default)
    {
        var envelope = new CognitiveEnvelope(
            CorrelationId: Guid.NewGuid().ToString("N")[..12],
            Topic: topic,
            Payload: payload,
            Timestamp: DateTimeOffset.UtcNow,
            SystemEntropy: GlobalEntropy
        );

        var writer = priority switch
        {
            CognitivePriority.Critical => _criticalChannel.Writer,
            CognitivePriority.High => _highChannel.Writer,
            _ => _normalChannel.Writer
        };

        await writer.WriteAsync(envelope, ct);
        _signal.Release();
    }

    public void UpdateGlobalEntropy(float entropy)
    {
        GlobalEntropy = Math.Clamp(entropy, 0.0f, 1.0f);
    }

    private async Task DispatchLoopAsync()
    {
        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _signal.WaitAsync(token);

                if (_criticalChannel.Reader.TryRead(out var envelope) ||
                    _highChannel.Reader.TryRead(out envelope) ||
                    _normalChannel.Reader.TryRead(out envelope))
                {
                    await ProcessEnvelopeAsync(envelope, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Штатное завершение работы шины
        }
    }

    private async Task ProcessEnvelopeAsync(CognitiveEnvelope envelope, CancellationToken token)
    {
        if (!_subscribers.TryGetValue(envelope.Topic, out var subscribers))
            return;

        List<(CognitivePriority Priority, Func<object, CancellationToken, Task> Handler)> snapshot;
        lock (subscribers)
        {
            snapshot = subscribers.ToList();
        }

        foreach (var (priority, handler) in snapshot)
        {
            if (GlobalEntropy > 0.80f && priority == CognitivePriority.BestEffort)
                continue;

            try
            {
                await handler(envelope.Payload, token);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[ОШИБКА АГЕНТА ШИНЫ] Топик: '{envelope.Topic}' -> Метод: '{handler.Method.Name}'");
                Console.WriteLine($"Детали: {ex.Message}\n{ex.StackTrace}");
                Console.ResetColor();

                DeadLetters.Enqueue(envelope.Topic, handler.Method.Name, ex.Message, envelope.Payload);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _signal.Release();
        try
        {
            await _dispatcherTask;
        }
        catch (OperationCanceledException)
        {
        }
        _signal.Dispose();
        _cts.Dispose();
    }
}