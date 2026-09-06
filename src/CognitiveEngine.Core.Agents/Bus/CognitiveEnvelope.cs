namespace CognitiveEngine.Core.Agents.Bus;

/// <summary>
/// Унифицированный мета-конверт сообщения агентной шины ECS.
/// Переносит полезную нагрузку вместе с контекстом трассировки, системной энтропией и глубиной рекурсивного арбитража.
/// </summary>
public sealed record CognitiveEnvelope(
    string CorrelationId,
    string Topic,
    object Payload,
    DateTimeOffset Timestamp,
    float SystemEntropy = 0.0f,
    int RefinementDepth = 0)
{
    /// <summary>
    /// Безопасное приведение полезной нагрузки к ожидаемому ссылочному типу.
    /// </summary>
    public T? As<T>() where T : class => Payload as T;

    /// <summary>
    /// Быстрое создание конверта с автоматической генерацией Correlation ID.
    /// </summary>
    public static CognitiveEnvelope Create(
        string topic,
        object payload,
        float systemEntropy = 0.0f,
        int refinementDepth = 0) =>
        new(
            CorrelationId: Guid.NewGuid().ToString("N")[..12],
            Topic: topic,
            Payload: payload,
            Timestamp: DateTimeOffset.UtcNow,
            SystemEntropy: systemEntropy,
            RefinementDepth: refinementDepth
        );
}