namespace CognitiveEngine.Infrastructure.Storage.Persistence;

using System.Runtime.InteropServices;
using System.Text.Json;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Domain.Models;
using CognitiveEngine.Infrastructure.Storage.Graph;

public sealed record NodeDiskRecord(
    string Id,
    string Content,
    Domain.Enums.NodeType Type,
    string Epoch,
    bool IsFrozen,
    float[] Vector
);

/// <summary>
/// Адаптер персистентного хранения: потоковая атомарная запись и пакетная вычитка JSONL
/// с защитой от лишних аллокаций памяти при работе с векторами.
/// </summary>
public sealed class VectorDatabaseAdapter
{
    private readonly string _storageDirectory;
    private readonly string _nodesPath;
    private readonly string _edgesPath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public VectorDatabaseAdapter(string storageDirectory = "./lance_store_v10")
    {
        _storageDirectory = storageDirectory;
        Directory.CreateDirectory(_storageDirectory);

        _nodesPath = Path.Combine(_storageDirectory, "nodes_snapshot.jsonl");
        _edgesPath = Path.Combine(_storageDirectory, "edges_snapshot.jsonl");
    }

    public async Task PersistSnapshotAsync(InMemoryGraphStore store, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            // 1. Потоковая запись рёбер
            var edges = await store.GetAllEdgesAsync(ct);
            string tempEdges = _edgesPath + ".tmp";
            await using (var writer = new StreamWriter(tempEdges, false))
            {
                foreach (var edge in edges)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(edge).AsMemory(), ct);
                }
            }
            File.Move(tempEdges, _edgesPath, overwrite: true);

            // 2. Потоковая запись узлов с оптимизацией извлечения векторов
            var nodes = await store.GetAllNodesAsync(ct);
            string tempNodes = _nodesPath + ".tmp";
            await using (var writer = new StreamWriter(tempNodes, false))
            {
                foreach (var node in nodes)
                {
                    float[] vecArray = MemoryMarshal.TryGetArray(node.Vector, out var seg) && seg.Array != null
                        ? seg.Array
                        : node.Vector.ToArray();

                    var record = new NodeDiskRecord(
                        node.Id,
                        node.Content,
                        node.Type,
                        node.Epoch,
                        node.IsFrozen,
                        vecArray
                    );
                    await writer.WriteLineAsync(JsonSerializer.Serialize(record).AsMemory(), ct);
                }
            }
            File.Move(tempNodes, _nodesPath, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task LoadSnapshotAsync(InMemoryGraphStore targetStore, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            // 1. Пакетная загрузка узлов
            if (File.Exists(_nodesPath))
            {
                var loadedNodes = new List<GraphNode>();
                using var reader = new StreamReader(_nodesPath);
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var record = JsonSerializer.Deserialize<NodeDiskRecord>(line);
                    if (record != null)
                    {
                        loadedNodes.Add(new GraphNode(
                            record.Id,
                            record.Content,
                            record.Type,
                            record.Epoch,
                            record.IsFrozen,
                            record.Vector
                        ));
                    }
                }
                await targetStore.AddNodesBatchAsync(loadedNodes, ct);
            }

            // 2. Пакетная загрузка рёбер (атомарным батчем)
            if (File.Exists(_edgesPath))
            {
                var loadedEdges = new List<GraphEdge>();
                using var reader = new StreamReader(_edgesPath);
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var edge = JsonSerializer.Deserialize<GraphEdge>(line);
                    if (edge != null)
                    {
                        loadedEdges.Add(edge);
                    }
                }

                await targetStore.AddEdgesBatchAsync(loadedEdges, ct);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }
}