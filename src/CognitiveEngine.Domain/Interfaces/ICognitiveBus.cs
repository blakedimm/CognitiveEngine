namespace CognitiveEngine.Domain.Interfaces;

using CognitiveEngine.Domain.Enums;

/// <summary>
/// Контракт реактивной шины сообщений агентного контура.
/// </summary>
public interface ICognitiveBus
{
    void Subscribe(string topic, Func<object, CancellationToken, Task> handler, CognitivePriority priority = CognitivePriority.Normal);
    ValueTask PublishAsync(string topic, object payload, CognitivePriority priority = CognitivePriority.Normal, CancellationToken ct = default);
}