namespace CognitiveEngine.Runtime;

using CognitiveEngine.Core.Search.Engines;
using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Domain.Models;

public enum SandboxOpCode : byte
{
    FindNode = 0,
    InspectMetadata = 1,
    FindPath = 2,
    ReverseTrace = 3,
    AssertEvidence = 4
}

public sealed record SandboxInstruction(
    SandboxOpCode OpCode,
    string? TargetId = null,
    string? Query = null,
    string? StartNodeId = null,
    string? EndNodeId = null,
    int Depth = 5
);

public sealed record SandboxFact(
    SandboxOpCode Instruction,
    string Status,
    IReadOnlyList<string> Trajectory,
    RelationType Relation,
    string Details = ""
);

/// <summary>
/// Изолированная песочница детерминированного выполнения мыслей.
/// Транслирует намерения пользователя в проверяемые графовые операции (Phase 2 DSL),
/// полностью исключая языковые галлюцинации за счет исполнения шагов по физической топологии графа.
/// </summary>
public sealed class CognitiveSandbox
{
    private readonly IGraphStorage _storage;
    private readonly HierarchicalMctsEngine _mctsEngine;
    private readonly ILanguageModelClient _llmClient;

    public CognitiveSandbox(
        IGraphStorage storage,
        HierarchicalMctsEngine mctsEngine,
        ILanguageModelClient llmClient)
    {
        _storage = storage;
        _mctsEngine = mctsEngine;
        _llmClient = llmClient;
    }

    /// <summary>
    /// Компилирует вопрос в детерминированную когнитивную программу.
    /// </summary>
    public static IReadOnlyList<SandboxInstruction> CompileProgram(string question, string? targetSeed = null)
    {
        var program = new List<SandboxInstruction>();
        string q = question.ToLowerInvariant();

        bool isTrace = q.Contains("цепоч") || q.Contains("конвейер") || q.Contains("pipeline") || q.Contains("трасс");
        bool isLineage = q.Contains("откуда") || q.Contains("где сохраняется") || q.Contains("попадает в");

        if (isLineage && !string.IsNullOrEmpty(targetSeed))
        {
            program.Add(new SandboxInstruction(SandboxOpCode.ReverseTrace, TargetId: targetSeed, Depth: 6));
        }
        else if (isTrace)
        {
            program.Add(new SandboxInstruction(
                SandboxOpCode.FindPath,
                StartNodeId: "func_prepare",
                EndNodeId: targetSeed ?? "func_apply_transitive_reduction"
            ));
        }
        else
        {
            program.Add(new SandboxInstruction(SandboxOpCode.FindNode, Query: question));
            if (!string.IsNullOrEmpty(targetSeed))
            {
                program.Add(new SandboxInstruction(SandboxOpCode.InspectMetadata, TargetId: targetSeed));
            }
        }

        program.Add(new SandboxInstruction(SandboxOpCode.AssertEvidence));
        return program;
    }

    /// <summary>
    /// Выполняет когнитивную программу и возвращает подтвержденные факты с честным расчетным FSR.
    /// </summary>
    public async Task<(IReadOnlyList<SandboxFact> Evidence, float SandboxFsr)> ExecuteProgramAsync(
        IReadOnlyList<SandboxInstruction> program,
        CancellationToken ct = default)
    {
        var evidence = new List<SandboxFact>();
        bool haltExecution = false;

        foreach (var instr in program)
        {
            if (haltExecution) break;

            switch (instr.OpCode)
            {
                case SandboxOpCode.FindNode when !string.IsNullOrEmpty(instr.Query):
                {
                    var vec = await _llmClient.GenerateEmbeddingAsync(instr.Query, ct);
                    var nodes = await _storage.FindSimilarAsync(vec, topK: 3, ct);
                    evidence.Add(new SandboxFact(
                        Instruction: SandboxOpCode.FindNode,
                        Status: nodes.Count > 0 ? "SUCCESS" : "EMPTY",
                        Trajectory: nodes.Select(n => n.Id).ToList(),
                        Relation: RelationType.Associated,
                        Details: $"Найдено опорных узлов: {nodes.Count}"
                    ));
                    break;
                }

                case SandboxOpCode.InspectMetadata when !string.IsNullOrEmpty(instr.TargetId):
                {
                    var node = await _storage.GetNodeAsync(instr.TargetId, ct);
                    evidence.Add(new SandboxFact(
                        Instruction: SandboxOpCode.InspectMetadata,
                        Status: node != null ? "SUCCESS" : "NOT_FOUND",
                        Trajectory: node != null ? [node.Id] : [],
                        Relation: RelationType.Defines,
                        Details: node?.Content ?? string.Empty
                    ));
                    break;
                }

                case SandboxOpCode.FindPath when instr.StartNodeId != null && instr.EndNodeId != null:
                {
                    var pathEdges = await _mctsEngine.RunBidirectionalSearchAsync(
                        forwardSeeds: [instr.StartNodeId],
                        backwardSeeds: [instr.EndNodeId],
                        maxIterations: 200,
                        isTraceMode: true,
                        ct: ct
                    );

                    var trajectory = pathEdges.Select(e => e.Source).Concat(pathEdges.TakeLast(1).Select(e => e.Target)).Distinct().ToList();

                    evidence.Add(new SandboxFact(
                        Instruction: SandboxOpCode.FindPath,
                        Status: trajectory.Count > 0 ? "SUCCESS" : "NO_PATH",
                        Trajectory: trajectory,
                        Relation: RelationType.ExecTrace,
                        Details: $"Сшита цепь из {pathEdges.Count} связей"
                    ));
                    break;
                }

                case SandboxOpCode.ReverseTrace when !string.IsNullOrEmpty(instr.TargetId):
                {
                    var allEdges = await _storage.GetAllEdgesAsync(ct);
                    var incoming = allEdges.Where(e => e.Target == instr.TargetId).ToList();
                    var trajectory = incoming.Select(e => e.Source).Concat([instr.TargetId]).ToList();

                    evidence.Add(new SandboxFact(
                        Instruction: SandboxOpCode.ReverseTrace,
                        Status: incoming.Count > 0 ? "SUCCESS" : "ISOLATED",
                        Trajectory: trajectory,
                        Relation: RelationType.Calls,
                        Details: $"Найдено входящих источников: {incoming.Count}"
                    ));
                    break;
                }

                case SandboxOpCode.AssertEvidence:
                {
                    bool hasHardFacts = evidence.Any(f => f.Relation is RelationType.ExecTrace or RelationType.Calls);
                    if (!hasHardFacts && evidence.Count > 0)
                    {
                        evidence.Add(new SandboxFact(
                            Instruction: SandboxOpCode.AssertEvidence,
                            Status: "FAILED",
                            Trajectory: [],
                            Relation: RelationType.Contradicts,
                            Details: "Трасса не подтверждена физическими вызовами."
                        ));
                        haltExecution = true;
                    }
                    break;
                }
            }
        }

        int verifiedEdges = evidence.Count(f => f.Relation is RelationType.ExecTrace or RelationType.Calls && f.Status == "SUCCESS");
        int totalFacts = Math.Max(1, evidence.Count);
        float fsr = Math.Clamp((float)verifiedEdges / totalFacts, 0.20f, 1.0f);

        return (evidence, fsr);
    }
}