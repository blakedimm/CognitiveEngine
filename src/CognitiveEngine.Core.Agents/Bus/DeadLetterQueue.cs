namespace CognitiveEngine.Core.Agents.Bus;

using System.Collections.Concurrent;

/// <summary>
/// Пакет сбоя обработки события агентом.
/// </summary>
public sealed record DeadLetterPacket(
    string OriginalTopic,
    string FailedAgent,
    string ErrorMessage,
    object Payload,
    DateTimeOffset FailedAt
);

/// <summary>
/// Потокобезопасная очередь изоляции упавших сообщений (Dead Letter Queue).
/// </summary>
public sealed class DeadLetterQueue
{
    private readonly ConcurrentQueue<DeadLetterPacket> _queue = new();

    public int Count => _queue.Count;

    public void Enqueue(string topic, string agentName, string error, object payload)
    {
        var packet = new DeadLetterPacket(topic, agentName, error, payload, DateTimeOffset.UtcNow);
        _queue.Enqueue(packet);
    }

    public IReadOnlyList<DeadLetterPacket> DrainAll()
    {
        var list = new List<DeadLetterPacket>();
        while (_queue.TryDequeue(out var packet))
        {
            list.Add(packet);
        }
        return list;
    }
}