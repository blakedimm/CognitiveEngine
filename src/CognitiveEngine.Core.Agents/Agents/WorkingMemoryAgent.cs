namespace CognitiveEngine.Core.Agents.Agents;

using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CognitiveEngine.Core.Agents.Bus;
using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Interfaces;

public sealed record EpisodicCase(
    string Question,
    IReadOnlyList<(string Relation, float Score)> PriorModifiers,
    float Fsr,
    DateTimeOffset CreatedAt,
    int Hits,
    int TotalRounds
);

public sealed record MemoryPriorsPayload(
    string SessionId,
    string Question,
    IReadOnlyDictionary<RelationType, float> RelationBoosts,
    float FamiliarityScore
);

/// <summary>
/// Ядро инкрементального обучения и ассоциативной памяти (Hippocampus).
/// Реализует кривую забывания Эббингауза, дисковую персистентность правил
/// и снижение глобальной энтропии при распознавании знакомых архитектурных топологий.
/// </summary>
public sealed partial class WorkingMemoryAgent
{
    private readonly ICognitiveBus _bus;
    private readonly List<EpisodicCase> _episodicMemory = new();
    private readonly ConcurrentDictionary<string, int> _failureRegistry = new();
    private readonly string _storagePath;
    private readonly object _lock = new();

    private const float DecayRate = 0.08f;
    private const string DefaultRulesFile = "compiled_rules.json";

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "как", "где", "какие", "что", "кто", "чем", "связан", "связаны", "вызывает", "вызывают",
        "между", "через", "для", "при", "есть", "или", "под", "над", "модуль", "класс", "метод", "функция"
    };

    [GeneratedRegex(@"[a-zA-Z_][a-zA-Z0-9_]*|[а-яА-ЯёЁ]{3,}", RegexOptions.Compiled)]
    private static partial Regex SemanticTokensRegex();

    public WorkingMemoryAgent(ICognitiveBus bus, string storagePath = DefaultRulesFile)
    {
        _bus = bus;
        _storagePath = storagePath;

        LoadFromDisk();

        _bus.Subscribe("task_logic_ready", InjectMemoryPriorsAsync, CognitivePriority.Critical);
        _bus.Subscribe("facts_verified", ConsolidateExperienceAsync, CognitivePriority.BestEffort);
    }

    private static HashSet<string> ExtractSemanticTokens(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = SemanticTokensRegex().Matches(text);

        foreach (Match m in matches)
        {
            string val = m.Value.ToLowerInvariant();
            if (!StopWords.Contains(val))
            {
                tokens.Add(val);
            }
        }

        return tokens;
    }

    private static float ComputeJaccardSimilarity(string textA, string textB)
    {
        var setA = ExtractSemanticTokens(textA);
        var setB = ExtractSemanticTokens(textB);

        if (setA.Count == 0 || setB.Count == 0) return 0f;

        int intersection = setA.Count(setB.Contains);
        int union = setA.Count + setB.Count - intersection;
        return union > 0 ? (float)intersection / union : 0f;
    }

    private static float CalculateRetentionScore(EpisodicCase memory)
    {
        double ageHours = (DateTimeOffset.UtcNow - memory.CreatedAt).TotalHours;
        float recencyDecay = (float)Math.Exp(-DecayRate * ageHours);
        float usageFactor = Math.Clamp((float)memory.Hits / Math.Max(1, memory.TotalRounds), 0.1f, 2.0f);

        return memory.Fsr * recencyDecay * (0.6f + 0.4f * usageFactor);
    }

    private async Task InjectMemoryPriorsAsync(object payload, CancellationToken ct)
    {
        if (payload is not IdealContextPayload context)
            return;

        EpisodicCase? bestMatch = null;
        float maxSim = 0.0f;
        int matchIndex = -1;

        lock (_lock)
        {
            for (int i = 0; i < _episodicMemory.Count; i++)
            {
                var memory = _episodicMemory[i];
                float sim = ComputeJaccardSimilarity(context.Question, memory.Question);
                if (sim > maxSim && sim >= 0.40f)
                {
                    maxSim = sim;
                    bestMatch = memory;
                    matchIndex = i;
                }
            }

            if (bestMatch != null && matchIndex >= 0)
            {
                _episodicMemory[matchIndex] = bestMatch with
                {
                    Hits = bestMatch.Hits + 1,
                    TotalRounds = bestMatch.TotalRounds + 1
                };
            }
        }

        if (bestMatch != null)
        {
            float retention = CalculateRetentionScore(bestMatch);
            var boosts = new Dictionary<RelationType, float>();

            foreach (var (relStr, score) in bestMatch.PriorModifiers)
            {
                if (Enum.TryParse<RelationType>(relStr, true, out var rel))
                {
                    boosts[rel] = Math.Clamp(score * retention, 0.1f, 1.5f);
                }
            }

            float familiarity = Math.Clamp(maxSim * retention, 0.0f, 0.90f);

            if (_bus is ChannelCognitiveBus channelBus)
            {
                channelBus.UpdateGlobalEntropy(Math.Max(0.05f, channelBus.GlobalEntropy * (1.0f - familiarity * 0.5f)));
            }

            var priorsPayload = new MemoryPriorsPayload(
                SessionId: context.SessionId,
                Question: context.Question,
                RelationBoosts: boosts,
                FamiliarityScore: familiarity
            );

            await _bus.PublishAsync("memory_priors_ready", priorsPayload, CognitivePriority.High, ct);
        }
    }

    private Task ConsolidateExperienceAsync(object payload, CancellationToken ct)
    {
        if (payload is not FactsVerifiedPayload data)
            return Task.CompletedTask;

        string question = data.Question;
        float fsr = data.Metrics.Fsr;
        float entropyCollapse = data.Metrics.EntropyCollapse;

        if (fsr >= 0.75f && entropyCollapse >= 0.50f && data.Claims.Count > 0)
        {
            var modifiers = data.Claims
                .GroupBy(c => c.Relation)
                .Select(g => (Relation: g.Key.ToString(), Score: g.Average(c => c.FusedScore)))
                .ToList();

            lock (_lock)
            {
                int existingIdx = _episodicMemory.FindIndex(m => ComputeJaccardSimilarity(m.Question, question) > 0.80f);
                if (existingIdx >= 0)
                {
                    var existing = _episodicMemory[existingIdx];
                    _episodicMemory[existingIdx] = existing with
                    {
                        PriorModifiers = modifiers,
                        Fsr = Math.Max(existing.Fsr, fsr),
                        TotalRounds = existing.TotalRounds + 1
                    };
                }
                else
                {
                    _episodicMemory.Add(new EpisodicCase(
                        Question: question,
                        PriorModifiers: modifiers,
                        Fsr: fsr,
                        CreatedAt: DateTimeOffset.UtcNow,
                        Hits: 1,
                        TotalRounds: 1
                    ));
                }

                if (_episodicMemory.Count > 40)
                {
                    _episodicMemory.Sort((a, b) => CalculateRetentionScore(b).CompareTo(CalculateRetentionScore(a)));
                    _episodicMemory.RemoveRange(30, _episodicMemory.Count - 30);
                }

                SaveToDisk();
            }
        }
        else if (fsr < 0.45f)
        {
            _failureRegistry.AddOrUpdate(question, 1, (_, v) => v + 1);
        }

        return Task.CompletedTask;
    }

    private void SaveToDisk()
    {
        try
        {
            string json = JsonSerializer.Serialize(_episodicMemory, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch
        {
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                string json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<EpisodicCase>>(json);
                if (loaded != null)
                {
                    lock (_lock)
                    {
                        _episodicMemory.Clear();
                        _episodicMemory.AddRange(loaded);
                    }
                }
            }
        }
        catch
        {
        }
    }
}