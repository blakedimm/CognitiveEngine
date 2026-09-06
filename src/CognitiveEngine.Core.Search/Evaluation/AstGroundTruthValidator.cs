namespace CognitiveEngine.Core.Search.Evaluation;

using System.Text.RegularExpressions;
using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Epistemics;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Domain.Models;

public sealed partial class AstGroundTruthValidator
{
    private readonly IGraphStorage _storage;

    [GeneratedRegex(@"\b(func_[a-zA-Z0-9_]+|cls_[a-zA-Z0-9_]+|mod_[a-zA-Z0-9_]+)\b", RegexOptions.Compiled)]
    private static partial Regex QualifiedIdentifierRegex();

    [GeneratedRegex(@"\b([a-zA-Z0-9_]+(?:\.[a-zA-Z0-9_]+)?)\b", RegexOptions.Compiled)]
    private static partial Regex DottedTokenRegex();

    public AstGroundTruthValidator(IGraphStorage storage)
    {
        _storage = storage;
    }

    public async Task<PathValidityMetrics> EvaluatePvrAsync(string responseText, bool isRefusalScenario = false, CancellationToken ct = default)
    {
        string proseOnly = Regex.Replace(responseText, @"```mermaid[\s\S]*?```", string.Empty).Trim();

        var allNodes = await _storage.GetAllNodesAsync(ct);
        var nodeIndex = BuildCanonicalNodeIndex(allNodes);

        var claimedTransitions = ExtractClaimedTransitions(proseOnly, nodeIndex);
        var verifiedTransitions = new List<ClaimedTransition>(claimedTransitions.Count);
        int validCount = 0;

        foreach (var (src, tgt, relationText) in claimedTransitions)
        {
            var physicalEdge = await _storage.FindEdgeAsync(src, tgt, ct);
            bool exists = physicalEdge != null && (physicalEdge.Relation is RelationType.Calls or RelationType.ExecTrace);

            if (exists)
            {
                validCount++;
            }

            verifiedTransitions.Add(new ClaimedTransition(
                Source: src,
                Target: tgt,
                StatedRelation: relationText,
                ExistsInAst: exists,
                ActualRelation: physicalEdge?.Relation,
                ActualWeight: physicalEdge?.Weight ?? 0f
            ));
        }

        var mentionedTokens = QualifiedIdentifierRegex().Matches(proseOnly)
            .Select(m => m.Value)
            .Where(t => !t.Contains("_to_")) // Исключаем служебные токены роутера
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hallucinatedNodes = mentionedTokens
            .Where(t => !nodeIndex.ContainsKey(t))
            .ToList();

        int totalClaimed = claimedTransitions.Count;
        float basePvr;

        if (isRefusalScenario)
        {
            basePvr = totalClaimed == 0 ? 1.0f : Math.Max(0.0f, 1.0f - (totalClaimed * 0.5f));
        }
        else
        {
            basePvr = totalClaimed > 0 ? (float)validCount / totalClaimed : 0.0f;
        }

        // Штраф 20% за каждую галлюцинированную сущность
        float penalizedPvr = Math.Max(0.0f, basePvr - (hallucinatedNodes.Count * 0.20f));

        return new PathValidityMetrics(
            TotalClaimedTransitions: totalClaimed,
            ValidTransitions: validCount,
            InvalidTransitions: totalClaimed - validCount,
            Pvr: penalizedPvr,
            Transitions: verifiedTransitions,
            HallucinatedNodes: hallucinatedNodes
        );
    }

    public async Task<MultiHopEvaluationResult> EvaluateMultiHopAsync(
        string responseText,
        string startNodeHint,
        string targetNodeHint,
        int minHops = 2,
        CancellationToken ct = default)
    {
        string proseOnly = Regex.Replace(responseText, @"```mermaid[\s\S]*?```", string.Empty).Trim();

        var allNodes = await _storage.GetAllNodesAsync(ct);
        var nodeIndex = BuildCanonicalNodeIndex(allNodes);

        string canonicalStart = ResolveCanonicalId(startNodeHint, null, nodeIndex) ?? startNodeHint;
        string canonicalTarget = ResolveCanonicalId(targetNodeHint, null, nodeIndex) ?? targetNodeHint;

        var transitions = ExtractClaimedTransitions(proseOnly, nodeIndex);
        var validAdjacency = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (src, tgt, _) in transitions)
        {
            var edge = await _storage.FindEdgeAsync(src, tgt, ct);
            if (edge != null && (edge.Relation is RelationType.Calls or RelationType.ExecTrace))
            {
                if (!validAdjacency.TryGetValue(src, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    validAdjacency[src] = set;
                }
                set.Add(tgt);
            }
        }

        var trajectory = FindShortestPath(canonicalStart, canonicalTarget, validAdjacency);
        bool pathFound = trajectory.Count > 0;
        int hops = pathFound ? trajectory.Count - 1 : 0;
        float completeness = (pathFound && hops >= minHops) ? Math.Clamp((float)hops / minHops, 0.1f, 1.0f) : 0.0f;

        return new MultiHopEvaluationResult(
            StartNode: canonicalStart,
            TargetNode: canonicalTarget,
            RequiredMinHops: minHops,
            PathFound: pathFound && hops >= minHops,
            ActualHops: hops,
            Trajectory: trajectory,
            HopCompletenessRatio: completeness
        );
    }

    public async Task<DirectionalAccuracyResult> EvaluateDirectionAsync(
        string responseText,
        string subjectHint,
        bool isCallerQuery,
        CancellationToken ct = default)
    {
        string proseOnly = Regex.Replace(responseText, @"```mermaid[\s\S]*?```", string.Empty).Trim();

        var allNodes = await _storage.GetAllNodesAsync(ct);
        var nodeIndex = BuildCanonicalNodeIndex(allNodes);
        string canonicalSubject = ResolveCanonicalId(subjectHint, null, nodeIndex) ?? subjectHint;

        var transitions = ExtractClaimedTransitions(proseOnly, nodeIndex);
        int correct = 0;
        int inverted = 0;

        foreach (var (src, tgt, _) in transitions)
        {
            var edgeForward = await _storage.FindEdgeAsync(src, tgt, ct);
            var edgeBackward = await _storage.FindEdgeAsync(tgt, src, ct);

            bool isSubjectInvolved = tgt.Contains(canonicalSubject, StringComparison.OrdinalIgnoreCase) || 
                                     src.Contains(canonicalSubject, StringComparison.OrdinalIgnoreCase);

            if (!isSubjectInvolved) continue;

            if (isCallerQuery)
            {
                if (tgt.Contains(canonicalSubject, StringComparison.OrdinalIgnoreCase) && edgeForward != null) correct++;
                else if (src.Contains(canonicalSubject, StringComparison.OrdinalIgnoreCase) && edgeBackward != null) inverted++;
            }
            else
            {
                if (src.Contains(canonicalSubject, StringComparison.OrdinalIgnoreCase) && edgeForward != null) correct++;
                else if (tgt.Contains(canonicalSubject, StringComparison.OrdinalIgnoreCase) && edgeBackward != null) inverted++;
            }
        }

        int totalInvolved = correct + inverted;
        float score = totalInvolved > 0 ? (float)correct / totalInvolved : 0.0f;

        return new DirectionalAccuracyResult(
            SubjectNode: canonicalSubject,
            IsCallerQuery: isCallerQuery,
            CorrectDirectionCount: correct,
            InvertedDirectionCount: inverted,
            DirectionalAccuracyScore: score
        );
    }

    public RefusalEvaluationResult EvaluateRefusal(string responseText, bool shouldRefuse)
    {
        string text = responseText.ToLowerInvariant();
        bool statesIsolation =
            text.Contains("изолирован") ||
            text.Contains("не обнаружен") ||
            text.Contains("отсутствует") ||
            text.Contains("не найден") ||
            text.Contains("нет информаци") ||
            text.Contains("не вызывают") ||
            text.Contains("не связаны") ||
            text.Contains("не зависит") ||
            text.Contains("нет прямой связи") ||
            text.Contains("прямых связей нет") ||
            (text.Contains("связ") && (text.Contains("нет") || text.Contains("отсутств")));

        return new RefusalEvaluationResult(
            ShouldRefuse: shouldRefuse,
            ActuallyRefused: statesIsolation,
            RefusalConfidence: (shouldRefuse == statesIsolation) ? 1.0f : 0.0f,
            ReasoningSnippet: statesIsolation ? "Изоляция подтверждена." : "Попытка связать несвязанные узлы."
        );
    }

    private static Dictionary<string, string> BuildCanonicalNodeIndex(IReadOnlyList<GraphNode> nodes)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            index[node.Id] = node.Id;

            string shortName = node.Id;
            if (shortName.StartsWith("func_")) shortName = shortName[5..];
            else if (shortName.StartsWith("cls_")) shortName = shortName[4..];
            else if (shortName.StartsWith("mod_")) shortName = shortName[4..];

            int lastUnder = shortName.LastIndexOf('_');
            if (lastUnder >= 0 && lastUnder < shortName.Length - 1)
            {
                string leafName = shortName[(lastUnder + 1)..];
                if (!leafName.Equals("__init__", StringComparison.OrdinalIgnoreCase) && !index.ContainsKey(leafName))
                {
                    index[leafName] = node.Id;
                }
            }

            if (!index.ContainsKey(shortName))
            {
                index[shortName] = node.Id;
            }
        }
        return index;
    }

    public static string? ResolveCanonicalId(string token, string? scopeContext, Dictionary<string, string> nodeIndex)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        string clean = token.Trim().Trim('`', '\'', '"', '(', ')', '[', ']', '{', '}', ':', ';', ',');

        // 1. Точечная нотация
        if (clean.Contains('.'))
        {
            var dotParts = clean.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (dotParts.Length == 2)
            {
                string clsOrMod = dotParts[0].ToLowerInvariant();
                string member = dotParts[1].ToLowerInvariant();

                var dottedMatch = nodeIndex.Keys.FirstOrDefault(k =>
                    k.Contains(clsOrMod, StringComparison.OrdinalIgnoreCase) &&
                    k.EndsWith($"_{member}", StringComparison.OrdinalIgnoreCase));

                if (dottedMatch != null) return nodeIndex[dottedMatch];
            }
        }

        // 2. Изоляция токена __init__
        if (clean.Equals("__init__", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(scopeContext))
            {
                string classOnly = ExtractPureClass(scopeContext);
                var initMatch = nodeIndex.Keys.FirstOrDefault(k =>
                    k.EndsWith("___init__", StringComparison.OrdinalIgnoreCase) &&
                    k.Contains(classOnly, StringComparison.OrdinalIgnoreCase));

                if (initMatch != null) return nodeIndex[initMatch];
            }
            return null;
        }

        // 3. Прямое совпадение
        if (nodeIndex.TryGetValue(clean, out var id)) return id;
        if (nodeIndex.TryGetValue($"func_{clean}", out id)) return id;
        if (nodeIndex.TryGetValue($"mod_{clean}", out id)) return id;
        if (nodeIndex.TryGetValue($"cls_{clean}", out id)) return id;

        // 4. Поиск с контекстом родительского класса
        if (!string.IsNullOrEmpty(scopeContext))
        {
            string classOnly = ExtractPureClass(scopeContext);
            if (!string.IsNullOrEmpty(classOnly))
            {
                var contextualMatch = nodeIndex.Keys.FirstOrDefault(k =>
                    k.EndsWith($"_{clean}", StringComparison.OrdinalIgnoreCase) &&
                    k.Contains(classOnly, StringComparison.OrdinalIgnoreCase));

                if (contextualMatch != null) return nodeIndex[contextualMatch];
            }
        }

        // 5. Однозначный суффиксный поиск
        var fullSubMatch = nodeIndex.Keys.FirstOrDefault(k => k.EndsWith($"_{clean}", StringComparison.OrdinalIgnoreCase) || k.Equals(clean, StringComparison.OrdinalIgnoreCase));
        if (fullSubMatch != null) return nodeIndex[fullSubMatch];

        return null;
    }

    private static string ExtractPureClass(string scope)
    {
        string s = scope.ToLowerInvariant();
        if (s.Contains("graphsageencoder") || s.Contains("encoder")) return "graphsageencoder";
        if (s.Contains("linkpredictor") || s.Contains("predictor")) return "linkpredictor";
        if (s.Contains("graphtopologyoptimizer") || s.Contains("optimizer")) return "graphtopologyoptimizer";
        if (s.Contains("tensorizationpipeline") || s.Contains("pipeline")) return "tensorizationpipeline";
        return s.Replace("func_", "").Replace("cls_", "").Replace("mod_", "").Replace("___init__", "");
    }

    private static List<(string Source, string Target, string Relation)> ExtractClaimedTransitions(
        string text,
        Dictionary<string, string> nodeIndex)
    {
        var transitions = new List<(string Source, string Target, string Relation)>();
        var seen = new HashSet<(string, string)>();

        void TryAdd(string? rawSrc, string? rawTgt, string? scope, string rel)
        {
            if (string.IsNullOrWhiteSpace(rawSrc) || string.IsNullOrWhiteSpace(rawTgt)) return;
            string? srcId = ResolveCanonicalId(rawSrc, scope, nodeIndex);
            string? tgtId = ResolveCanonicalId(rawTgt, srcId ?? scope, nodeIndex);

            if (srcId != null && tgtId != null && !srcId.Equals(tgtId, StringComparison.OrdinalIgnoreCase))
            {
                if (srcId.StartsWith("cls_") && tgtId.StartsWith("cls_")) return;

                if (seen.Add((srcId, tgtId)))
                {
                    transitions.Add((srcId, tgtId, rel));
                }
            }
        }

        // 1. Стрелочные переходы X -> Y
        var arrowMatches = Regex.Matches(text, @"[`""]?([a-zA-Z0-9_\.]+)[`""]?\s*(?:->|-->|==>|=>)\s*[`""]?([a-zA-Z0-9_\.]+)[`""]?");
        foreach (Match m in arrowMatches)
        {
            TryAdd(m.Groups[1].Value, m.Groups[2].Value, null, "arrow");
        }

        // 2. Семантический разбор предложений
        string cleanText = text.Replace("`", " ").Replace("**", " ").Replace("*", " ");
        var sentences = cleanText.Split(['\n', '\r', '.', ';'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawSentence in sentences)
        {
            string sentence = rawSentence.Trim();
            if (sentence.Length < 10) continue;

            string sLower = sentence.ToLowerInvariant();

            bool isNegation = sLower.Contains("не имеет") || sLower.Contains("не вызывает") || 
                              sLower.Contains("нет прямой связи") || sLower.Contains("нет связ") || 
                              sLower.Contains("изолирован") || sLower.Contains("не связан");
            if (isNegation) continue;

            var tokens = DottedTokenRegex().Matches(sentence).Select(m => m.Value).ToList();
            if (tokens.Count < 2) continue;

            bool isCallContext = sLower.Contains("вызыв") || sLower.Contains("call") || 
                                 sLower.Contains("обращ") || sLower.Contains("доход");
            if (!isCallContext) continue;

            bool isStrictPassive = sLower.Contains("вызывается из");

            // Ищем контекстный класс для текущего предложения
            string? detectedScope = tokens.FirstOrDefault(w => 
                w.Contains("predictor", StringComparison.OrdinalIgnoreCase) || 
                w.Contains("encoder", StringComparison.OrdinalIgnoreCase) ||
                w.Contains("optimizer", StringComparison.OrdinalIgnoreCase) ||
                w.Contains("pipeline", StringComparison.OrdinalIgnoreCase));

            for (int i = 0; i < tokens.Count - 1; i++)
            {
                string t1 = tokens[i];
                string t2 = tokens[i + 1];

                // Локальный скоуп строго привязывается к токену t1
                string? localScope = t1.Contains("encoder", StringComparison.OrdinalIgnoreCase) ? "graphsageencoder" :
                                     t1.Contains("predictor", StringComparison.OrdinalIgnoreCase) ? "linkpredictor" : detectedScope;

                string? c1 = ResolveCanonicalId(t1, localScope, nodeIndex);
                string? c2 = ResolveCanonicalId(t2, c1 ?? localScope, nodeIndex);

                if (c1 != null && c2 != null && !c1.Equals(c2, StringComparison.OrdinalIgnoreCase))
                {
                    if (isStrictPassive)
                    {
                        TryAdd(c2, c1, localScope, "sentence_passive_call");
                    }
                    else
                    {
                        TryAdd(c1, c2, localScope, "sentence_active_call");
                    }
                }
            }
        }

        return transitions;
    }

    private static List<string> FindShortestPath(
        string start,
        string target,
        Dictionary<string, HashSet<string>> adj)
    {
        if (start.Equals(target, StringComparison.OrdinalIgnoreCase)) return [start];

        var queue = new Queue<List<string>>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        queue.Enqueue([start]);

        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            string curr = path[^1];

            if (curr.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            if (adj.TryGetValue(curr, out var neighbors))
            {
                foreach (var nxt in neighbors)
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

        return [];
    }
}