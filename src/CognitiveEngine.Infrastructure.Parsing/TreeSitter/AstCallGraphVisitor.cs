namespace CognitiveEngine.Infrastructure.Parsing.TreeSitter;

using System.Text.Json;
using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Domain.Models;

public sealed record ModuleParseResult(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges
);

/// <summary>
/// Агрегатор топологии кода: обходит синтаксические блоки модулей, формирует граф вызовов (Call Graph),
/// строит строгую безаварийную иерархию (модули -> классы -> методы) и пакетно рассчитывает векторные паспорта узлов.
/// </summary>
public sealed class AstCallGraphVisitor
{
    private readonly ILanguageModelClient _llmClient;

    public AstCallGraphVisitor(ILanguageModelClient llmClient)
    {
        _llmClient = llmClient;
    }

    public async Task<ModuleParseResult> ProcessModuleAsync(
        string filePath,
        string sourceCode,
        IReadOnlyDictionary<string, string> globalFunctionLookup,
        CancellationToken ct = default)
    {
        string fileName = Path.GetFileName(filePath);
        string moduleName = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
        string modNodeId = $"mod_{moduleName}";

        var rawBlocks = TreeSitterParser.ParseModuleFunctions(sourceCode);

        // Вспомогательные контейнеры предварительной разметки до расчёта векторов
        var pendingNodes = new List<(string Id, string Content, NodeType Type, string SemanticText)>();
        var edges = new List<GraphEdge>();

        // 1. Разметка узла модуля
        string modContent = $"Модуль {fileName}: Исходный программный файл системы.";
        pendingNodes.Add((modNodeId, modContent, NodeType.Module, modContent));

        var seenClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 2. Предварительный проход: сбор классов и методов с устранением коллизий
        var functionLookupInModule = new List<(string FuncId, string? ParentClass, FunctionAnatomy Anatomy)>();

        foreach (var block in rawBlocks)
        {
            // ООП-слой: классы
            if (!string.IsNullOrEmpty(block.ParentClass) && seenClasses.Add(block.ParentClass))
            {
                string clsNodeId = $"cls_{moduleName}_{block.ParentClass.ToLowerInvariant()}";
                string clsContent = $"Класс {block.ParentClass} в модуле {fileName}. Инкапсулирует методы и состояние.";
                pendingNodes.Add((clsNodeId, clsContent, NodeType.Class, clsContent));

                edges.Add(new GraphEdge(
                    Id: Guid.NewGuid(),
                    Source: modNodeId,
                    Target: clsNodeId,
                    Relation: RelationType.Contains,
                    Epistemic: EpistemicClass.Hard,
                    Weight: 1.0f
                ));
            }

            // Функциональный слой: уникальный ID без риска коллизий __init__
            string funcName = block.Name.ToLowerInvariant();
            string funcNodeId = !string.IsNullOrEmpty(block.ParentClass)
                ? $"func_{moduleName}_{block.ParentClass.ToLowerInvariant()}_{funcName}"
                : $"func_{moduleName}_{funcName}";

            var anatomy = DeepAnatomyExtractor.Extract(block.SourceCode);
            string funcContent = JsonSerializer.Serialize(anatomy);

            string semanticAnchor = $"Функция {block.Name}() в {fileName}. Аргументы: [{string.Join(", ", anatomy.Inputs)}]. Вызовы: [{string.Join(", ", anatomy.Calls)}].";
            pendingNodes.Add((funcNodeId, funcContent, NodeType.Function, semanticAnchor));

            string parentId = !string.IsNullOrEmpty(block.ParentClass)
                ? $"cls_{moduleName}_{block.ParentClass.ToLowerInvariant()}"
                : modNodeId;

            edges.Add(new GraphEdge(
                Id: Guid.NewGuid(),
                Source: parentId,
                Target: funcNodeId,
                Relation: RelationType.Contains,
                Epistemic: EpistemicClass.Hard,
                Weight: 1.0f
            ));

            functionLookupInModule.Add((funcNodeId, block.ParentClass, anatomy));
        }

        // 3. Пакетный расчёт эмбеддингов за 1 сетевой вызов (Batch Embedding)
        var textsToEmbed = pendingNodes.Select(p => p.SemanticText).ToList();
        var vectors = await _llmClient.GenerateBatchEmbeddingsAsync(textsToEmbed, ct);

        var finalNodes = new List<GraphNode>(pendingNodes.Count);
        for (int i = 0; i < pendingNodes.Count; i++)
        {
            var p = pendingNodes[i];
            var vector = (i < vectors.Count) ? vectors[i] : (ReadOnlyMemory<float>)new float[1024];

            finalNodes.Add(new GraphNode(
                Id: p.Id,
                Content: p.Content,
                Type: p.Type,
                Epoch: "ast_v10",
                IsFrozen: true,
                Vector: vector
            ));
        }

        // 4. Построение вызовов (Call Graph) с контекстным разрешением неоднозначности
        foreach (var (funcNodeId, parentClass, anatomy) in functionLookupInModule)
        {
            foreach (var callee in anatomy.Calls)
            {
                string calleeLower = callee.ToLowerInvariant();
                string? targetFuncId = null;

                // Приоритет 1: Вызов метода того же класса
                if (!string.IsNullOrEmpty(parentClass) &&
                    globalFunctionLookup.TryGetValue($"{parentClass.ToLowerInvariant()}.{calleeLower}", out var classFuncId))
                {
                    targetFuncId = classFuncId;
                }
                // Приоритет 2: Вызов функции того же модуля
                else if (globalFunctionLookup.TryGetValue($"{moduleName}.{calleeLower}", out var modFuncId))
                {
                    targetFuncId = modFuncId;
                }
                // Приоритет 3: Глобальный уникальный вызов
                else if (globalFunctionLookup.TryGetValue(calleeLower, out var globalFuncId))
                {
                    targetFuncId = globalFuncId;
                }

                if (targetFuncId != null && !targetFuncId.Equals(funcNodeId, StringComparison.OrdinalIgnoreCase))
                {
                    edges.Add(new GraphEdge(
                        Id: Guid.NewGuid(),
                        Source: funcNodeId,
                        Target: targetFuncId,
                        Relation: RelationType.Calls,
                        Epistemic: EpistemicClass.Hard,
                        Weight: 1.0f
                    ));
                }
            }
        }

        return new ModuleParseResult(finalNodes, edges);
    }
}