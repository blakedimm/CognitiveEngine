namespace CognitiveEngine.Infrastructure.Storage.Persistence;

using System.Text.Json;
using CognitiveEngine.Domain.Models;

public sealed record WalTransaction(
    string TxId,
    string Status,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    DateTimeOffset Timestamp
);

/// <summary>
/// Атомарный журнал упреждающей записи (WAL) для изоляции транзакций персистентности.
/// </summary>
public sealed class WriteAheadLog : IAsyncDisposable
{
    private readonly FileStream _fileStream;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public WriteAheadLog(string walPath = "graph_journal.wal")
    {
        _fileStream = new FileStream(walPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _writer = new StreamWriter(_fileStream) { AutoFlush = true };
    }

    public async Task<string> PrepareAsync(
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            string txId = Guid.NewGuid().ToString("N")[..8];
            var tx = new WalTransaction(txId, "PREPARE", nodes, edges, DateTimeOffset.UtcNow);

            string line = JsonSerializer.Serialize(tx);
            await _writer.WriteLineAsync(line.AsMemory(), ct);
            return txId;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CommitAsync(string txId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var commitRecord = new { TxId = txId, Status = "COMMITTED", Timestamp = DateTimeOffset.UtcNow };
            string line = JsonSerializer.Serialize(commitRecord);
            await _writer.WriteLineAsync(line.AsMemory(), ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        await _fileStream.DisposeAsync();
        _lock.Dispose();
    }
}