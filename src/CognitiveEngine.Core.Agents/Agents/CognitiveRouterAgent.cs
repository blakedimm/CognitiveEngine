namespace CognitiveEngine.Core.Agents.Agents;

using System.Text.RegularExpressions;
using CognitiveEngine.Core.Agents.Bus;
using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Domain.Models;

public enum QueryIntent
{
    MultiHopBridge = 0,
    BidirectionalTrace = 0,
    CallersTrace = 1,
    CalleesTrace = 2,
    ExceptionBoundary = 3,
    ArchitectureOverview = 4
}

public sealed partial class CognitiveRouterAgent
{
    private readonly ICognitiveBus _bus;
    private readonly IGraphStorage _storage;
    private readonly ILanguageModelClient _llmClient;

    [GeneratedRegex(@"\b(func_\w+|cls_\w+|mod_\w+|[A-Z][A-Za-z0-9_]{2,}|[a-z]{3,}_[a-z0-9_]+)\b", RegexOptions.Compiled)]
    private static partial Regex IdentifierRegex();

    public CognitiveRouterAgent(ICognitiveBus bus, IGraphStorage storage, ILanguageModelClient llmClient)
    {
        _bus = bus;
        _storage = storage;
        _llmClient = llmClient;

        _bus.Subscribe("task_route_context", HandleRoutingRequestAsync, CognitivePriority.Critical);
    }

    public async Task HandleRoutingRequestAsync(object payload, CancellationToken ct)
    {
        string question;
        string sessionId;

        if (payload is RouteTaskPayload rtp)
        {
            question = rtp.Question;
            sessionId = rtp.SessionId;
        }
        else if (payload is ValueTuple<string, string> tuple)
        {
            question = tuple.Item1;
            sessionId = tuple.Item2;
        }
        else return;

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("    -> [Router] Интеллектуальный разбор топологии и интента...");
        Console.ResetColor();

        var intent = DetectIntent(question);

        var allNodes = await _storage.GetAllNodesAsync(ct);
        var nodeMap = allNodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var primarySeeds = await ResolveSeedsWithContextAsync(question, allNodes, ct);

        var allEdges = await _storage.GetAllEdgesAsync(ct);

        // ФИКС БАГА 1: Если вопрос о связи/пути между двумя удалёнными узлами, проверяем достижимость
        bool isBridgeOrRefusalQuery = intent is QueryIntent.MultiHopBridge or QueryIntent.ExceptionBoundary;
        bool isPathActuallyPossible = true;

        if (isBridgeOrRefusalQuery && primarySeeds.Count >= 2)
        {
            isPathActuallyPossible = CheckSeedsConnected(primarySeeds, allEdges);
        }

        var (subgraphEdges, expandedNodes) = isPathActuallyPossible
            ? BuildControlledSubgraph(primarySeeds, allEdges, intent)
            : (new List<GraphEdge>(), new HashSet<string>(primarySeeds, StringComparer.OrdinalIgnoreCase));

        var nodePressures = ComputePersonalizedPressures(primarySeeds, expandedNodes, subgraphEdges);

        var activeContent = new Dictionary<string, string>();
        foreach (var nid in expandedNodes)
        {
            if (nodeMap.TryGetValue(nid, out var node))
            {
                activeContent[nid] = node.Content;
            }
        }

        bool isHeavyTrace = intent is QueryIntent.MultiHopBridge or QueryIntent.CallersTrace;
        int budget = (!isPathActuallyPossible) ? 0 : (isHeavyTrace ? 450 : 200);
        var plane = (subgraphEdges.Count < 3 && !isHeavyTrace)
            ? ExecutionPlane.System1_Intuition
            : ExecutionPlane.System2_Deliberation;

        var payloadCompiled = new IdealContextPayload(
            SessionId: sessionId,
            Question: question,
            Domain: _storage.Domain,
            Mode: plane,
            MctsBudget: budget,
            Seeds: primarySeeds,
            SubgraphEdges: subgraphEdges,
            NodePressures: nodePressures,
            ActiveNodesContent: activeContent
        );

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"    -> [Router] Интент: {intent} | Опорных узлов: {primarySeeds.Count} | Подграф: {subgraphEdges.Count} связей.");
        Console.ResetColor();

        await _bus.PublishAsync("task_logic_ready", payloadCompiled, CognitivePriority.High, ct);
    }

    public static QueryIntent DetectIntent(string question)
    {
        string q = question.ToLowerInvariant();

        // 1. Входящие вызовы (In-Degree: кто вызывает / из каких конструкторов)
        if (q.Contains("вызыва") && (q.Contains("кто") || q.Contains("какие") || q.Contains("где") || q.Contains("кем") || q.Contains("входящ") || q.Contains("из каких")))
            return QueryIntent.CallersTrace;

        // 2. Исходящие вызовы (Out-Degree: кого вызывает / куда ведёт)
        if (q.Contains("куда") || q.Contains("исходящ") || (q.Contains("что") && q.Contains("вызыва")))
            return QueryIntent.CalleesTrace;

        // 3. Сквозная многозвенная транзитивность / вопрос о связи двух сущностей
        bool hasPathMarkers = (q.Contains("от") && (q.Contains("до") || q.Contains("к"))) || q.Contains("через какие") || q.Contains("поток данных");
        bool isConnectionQuery = q.Contains("как связан") || q.Contains("связан") || q.Contains("связь между");
        if (hasPathMarkers || isConnectionQuery || q.Contains("цепочк") || q.Contains("трассир") || q.Contains("транзитив") || q.Contains("конвейер"))
            return QueryIntent.MultiHopBridge;

        // 4. Границы исключений и валидации
        if (q.Contains("исключен") || q.Contains("ошибк") || q.Contains("изолир") || 
            q.Contains("except") || q.Contains("валидац") || q.Contains("try_catch"))
            return QueryIntent.ExceptionBoundary;

        return QueryIntent.ArchitectureOverview;
    }

    private static bool CheckSeedsConnected(IReadOnlyList<string> seeds, IReadOnlyList<GraphEdge> allEdges)
    {
        if (seeds.Count < 2) return true;

        var adj = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in allEdges)
        {
            if (!adj.TryGetValue(edge.Source, out var srcList))
            {
                srcList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                adj[edge.Source] = srcList;
            }
            srcList.Add(edge.Target);

            if (!adj.TryGetValue(edge.Target, out var tgtList))
            {
                tgtList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                adj[edge.Target] = tgtList;
            }
            tgtList.Add(edge.Source);
        }

        string start = seeds[0];
        var targetSet = new HashSet<string>(seeds.Skip(1), StringComparer.OrdinalIgnoreCase);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var queue = new Queue<string>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            string curr = queue.Dequeue();
            if (targetSet.Contains(curr)) return true;

            if (adj.TryGetValue(curr, out var neighbors))
            {
                foreach (var nxt in neighbors)
                {
                    if (visited.Add(nxt)) queue.Enqueue(nxt);
                }
            }
        }

        return false;
    }

    private async Task<List<string>> ResolveSeedsWithContextAsync(
        string question,
        IReadOnlyList<GraphNode> allNodes,
        CancellationToken ct)
    {
        var resolvedSeeds = new List<string>();
        var matches = IdentifierRegex().Matches(question);
        var candidateTokens = matches.Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();

        ReadOnlyMemory<float> questionEmbedding = default;

        foreach (var token in candidateTokens)
        {
            var exact = allNodes.FirstOrDefault(n => n.Id.Equals(token, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                if (!resolvedSeeds.Contains(exact.Id)) resolvedSeeds.Add(exact.Id);
                continue;
            }

            var candidates = allNodes.Where(n =>
                n.Id.EndsWith($"_{token}", StringComparison.OrdinalIgnoreCase) ||
                n.Id.Contains($"_{token}_", StringComparison.OrdinalIgnoreCase) ||
                n.Id.Equals($"cls_{token}", StringComparison.OrdinalIgnoreCase) ||
                n.Id.Equals($"func_{token}", StringComparison.OrdinalIgnoreCase) ||
                n.Id.Equals($"mod_{token}", StringComparison.OrdinalIgnoreCase)).ToList();

            if (candidates.Count == 1)
            {
                if (!resolvedSeeds.Contains(candidates[0].Id)) resolvedSeeds.Add(candidates[0].Id);
            }
            else if (candidates.Count > 1)
            {
                if (questionEmbedding.IsEmpty)
                {
                    questionEmbedding = await _llmClient.GenerateEmbeddingAsync(question, ct);
                }

                var bestCandidate = candidates
                    .OrderByDescending(c => CalculateCosine(questionEmbedding.Span, c.Vector.Span))
                    .First();

                if (!resolvedSeeds.Contains(bestCandidate.Id)) resolvedSeeds.Add(bestCandidate.Id);
            }
        }

        if (resolvedSeeds.Count < 2)
        {
            if (questionEmbedding.IsEmpty)
            {
                questionEmbedding = await _llmClient.GenerateEmbeddingAsync(question, ct);
            }

            var semanticNodes = await _storage.FindSimilarAsync(questionEmbedding, topK: 4, ct);
            foreach (var node in semanticNodes)
            {
                if (!resolvedSeeds.Contains(node.Id)) resolvedSeeds.Add(node.Id);
            }
        }

        return resolvedSeeds;
    }

    private static (List<GraphEdge> Edges, HashSet<string> Nodes) BuildControlledSubgraph(
        IReadOnlyList<string> seeds,
        IReadOnlyList<GraphEdge> allEdges,
        QueryIntent intent)
    {
        const int maxFanOutPerNode = 12;
        const int maxHops = 3;

        var subgraph = new List<GraphEdge>();
        var expandedNodes = new HashSet<string>(seeds, StringComparer.OrdinalIgnoreCase);
        var currentFrontier = new HashSet<string>(seeds, StringComparer.OrdinalIgnoreCase);

        var outEdges = new Dictionary<string, List<GraphEdge>>(StringComparer.OrdinalIgnoreCase);
        var inEdges = new Dictionary<string, List<GraphEdge>>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in allEdges)
        {
            if (!outEdges.TryGetValue(edge.Source, out var outList))
            {
                outList = [];
                outEdges[edge.Source] = outList;
            }
            outList.Add(edge);

            if (!inEdges.TryGetValue(edge.Target, out var inList))
            {
                inList = [];
                inEdges[edge.Target] = inList;
            }
            inList.Add(edge);
        }

        for (int hop = 1; hop <= maxHops; hop++)
        {
            var nextFrontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            float decay = hop switch { 1 => 1.0f, 2 => 0.70f, _ => 0.45f };

            foreach (var node in currentFrontier)
            {
                var candidateEdges = new List<GraphEdge>();

                if (intent == QueryIntent.CallersTrace)
                {
                    if (inEdges.TryGetValue(node, out var incoming)) candidateEdges.AddRange(incoming);
                    if (outEdges.TryGetValue(node, out var outgoing)) candidateEdges.AddRange(outgoing.Take(3));
                }
                else if (intent == QueryIntent.CalleesTrace)
                {
                    if (outEdges.TryGetValue(node, out var outgoing)) candidateEdges.AddRange(outgoing);
                    if (inEdges.TryGetValue(node, out var incoming)) candidateEdges.AddRange(incoming.Take(3));
                }
                else
                {
                    if (outEdges.TryGetValue(node, out var outgoing)) candidateEdges.AddRange(outgoing);
                    if (inEdges.TryGetValue(node, out var incoming)) candidateEdges.AddRange(incoming.Take(5));
                }

                var filteredEdges = candidateEdges
                    .Where(e => !subgraph.Any(existing => existing.Id == e.Id))
                    .OrderByDescending(e => e.Relation is RelationType.Calls or RelationType.ExecTrace)
                    .ThenByDescending(e => e.Weight)
                    .Take(maxFanOutPerNode);

                foreach (var edge in filteredEdges)
                {
                    subgraph.Add(edge with { Weight = edge.Weight * decay });
                    string neighbor = edge.Source.Equals(node, StringComparison.OrdinalIgnoreCase) ? edge.Target : edge.Source;

                    if (expandedNodes.Add(neighbor))
                    {
                        nextFrontier.Add(neighbor);
                    }
                }
            }

            if (nextFrontier.Count == 0) break;
            currentFrontier = nextFrontier;
        }

        return (subgraph, expandedNodes);
    }

    private static Dictionary<string, float> ComputePersonalizedPressures(
        IReadOnlyList<string> seeds,
        HashSet<string> nodes,
        List<GraphEdge> edges)
    {
        var pressures = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var degreeMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in edges)
        {
            degreeMap[edge.Source] = degreeMap.GetValueOrDefault(edge.Source) + 1;
            degreeMap[edge.Target] = degreeMap.GetValueOrDefault(edge.Target) + 1;
        }

        int maxDegree = degreeMap.Values.Count > 0 ? degreeMap.Values.Max() : 1;

        foreach (var node in nodes)
        {
            float degScore = (float)degreeMap.GetValueOrDefault(node, 0) / maxDegree;
            float seedBonus = seeds.Contains(node, StringComparer.OrdinalIgnoreCase) ? 0.50f : 0.0f;
            pressures[node] = Math.Clamp(degScore * 0.5f + seedBonus, 0.05f, 1.0f);
        }

        return pressures;
    }

    private static float CalculateCosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;
        float dot = 0f, magA = 0f, magB = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        float denom = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return denom > 0f ? dot / denom : 0f;
    }
}