namespace CognitiveEngine.Runtime;

using System.Collections.Concurrent;
using System.Diagnostics;
using CognitiveEngine.Core.Agents.Agents;
using CognitiveEngine.Core.Agents.Bus;
using CognitiveEngine.Core.Search.Engines;
using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Domain.Models;
using CognitiveEngine.Infrastructure.ML.Onnx;

public sealed class SemanticCore : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CognitiveExecutionResult>> _pendingQueries = new();
    private readonly ConcurrentDictionary<string, Stopwatch> _sessionStopwatches = new();
    private readonly ConcurrentDictionary<string, long> _symbolicLatencies = new();

    public ChannelCognitiveBus Bus { get; }
    public IGraphStorage Storage { get; }
    public OnnxLinkPredictor GnnPredictor { get; }
    public HierarchicalMctsEngine MctsEngine { get; }
    public ILanguageModelClient LlmClient { get; }
    public CognitiveSandbox Sandbox { get; }
    public TelemetryTracker Telemetry { get; }

    public CognitiveRouterAgent RouterAgent { get; }
    public WorkingMemoryAgent WorkingMemory { get; }
    public SkepticAgent SkepticAgent { get; }
    public CriticAgent CriticAgent { get; }
    public LogicAgent LogicAgent { get; }
    public FactCheckerAgent FactChecker { get; }
    public NarrativeAgent NarrativeAgent { get; }

    public SemanticCore(
        IGraphStorage storage,
        ILanguageModelClient llmClient,
        string gnnPredictorPath = "gnn_predictor.onnx")
    {
        Storage = storage;
        LlmClient = llmClient;

        Bus = new ChannelCognitiveBus();
        Telemetry = new TelemetryTracker();
        GnnPredictor = new OnnxLinkPredictor(gnnPredictorPath);

        MctsEngine = new HierarchicalMctsEngine(Storage, GnnPredictor);
        Sandbox = new CognitiveSandbox(Storage, MctsEngine, LlmClient);

        RouterAgent = new CognitiveRouterAgent(Bus, Storage, LlmClient);
        WorkingMemory = new WorkingMemoryAgent(Bus);
        SkepticAgent = new SkepticAgent(Bus);
        CriticAgent = new CriticAgent(Bus);
        LogicAgent = new LogicAgent(Bus, Storage, GnnPredictor, MctsEngine, SkepticAgent, CriticAgent);
        FactChecker = new FactCheckerAgent(Bus, Storage);
        NarrativeAgent = new NarrativeAgent(Bus, LlmClient);

        Bus.Subscribe("facts_verified", OnFactsVerifiedAsync, CognitivePriority.Critical);
        Bus.Subscribe("pipeline_completed", OnPipelineCompletedAsync, CognitivePriority.Critical);
    }

    private Task OnFactsVerifiedAsync(object payload, CancellationToken ct)
    {
        if (payload is FactsVerifiedPayload data && _sessionStopwatches.TryGetValue(data.SessionId, out var sw))
        {
            _symbolicLatencies[data.SessionId] = sw.ElapsedMilliseconds;
        }
        return Task.CompletedTask;
    }

    private Task OnPipelineCompletedAsync(object payload, CancellationToken ct)
    {
        if (payload is CognitiveExecutionResult result &&
            _pendingQueries.TryRemove(result.SessionId, out var tcs))
        {
            long symbolicMs = _symbolicLatencies.TryRemove(result.SessionId, out var ms) ? ms : 0;
            _sessionStopwatches.TryRemove(result.SessionId, out _);

            var enrichedResult = result with { SymbolicLatencyMs = symbolicMs };
            tcs.TrySetResult(enrichedResult);
        }

        return Task.CompletedTask;
    }

    public async Task<CognitiveExecutionResult> QueryAsync(string question, CancellationToken ct = default)
    {
        string sessionId = Guid.NewGuid().ToString("N")[..12];
        var tcs = new TaskCompletionSource<CognitiveExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingQueries[sessionId] = tcs;

        var stopwatch = Stopwatch.StartNew();
        _sessionStopwatches[sessionId] = stopwatch;

        await Bus.PublishAsync("task_route_context", new RouteTaskPayload(question, sessionId), CognitivePriority.Critical, ct);

        using var registration = ct.Register(() =>
        {
            if (_pendingQueries.TryRemove(sessionId, out var pending))
            {
                pending.TrySetCanceled(ct);
            }
            _sessionStopwatches.TryRemove(sessionId, out _);
            _symbolicLatencies.TryRemove(sessionId, out _);
        });

        var result = await tcs.Task;
        stopwatch.Stop();

        Telemetry.RecordSession(
            sessionId: sessionId,
            metrics: result.Metrics,
            latencyMs: stopwatch.ElapsedMilliseconds,
            nodesActivated: result.VerifiedClaims.Count * 2,
            edgesTraversed: result.VerifiedClaims.Count,
            rawPromptTokens: question.Length,
            condensedProofTokens: result.FinalAnswer.Length
        );

        return result;
    }

    public async Task SeedCoreArchitectureAsync(CancellationToken ct = default)
    {
        // Семенируются только системные модули рантайма, без вымышленных функций
        var seedNodes = new[]
        {
            new GraphNode("mod_semantic_core", "Модуль оркестратора SemanticCore и мультиагентной шины.", NodeType.Module),
            new GraphNode("mod_storage", "Инфраструктурный модуль хранения таблиц LanceDB и графа знаний.", NodeType.Module),
            new GraphNode("cls_semantic_core", "Класс SemanticCore: координатор System 2 и турнира гипотез.", NodeType.Class)
        };

        await Storage.AddNodesBatchAsync(seedNodes, ct);

        var seedEdges = new[]
        {
            new GraphEdge(Guid.NewGuid(), "mod_semantic_core", "cls_semantic_core", RelationType.Contains, EpistemicClass.Hard, 1.0f)
        };

        foreach (var edge in seedEdges)
        {
            await Storage.AddEdgeAsync(edge, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Bus.DisposeAsync();
        GnnPredictor.Dispose();
    }
}