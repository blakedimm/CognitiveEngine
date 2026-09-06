namespace CognitiveEngine.Domain.Enums;

/// <summary>
/// Приоритет диспетчеризации событий в реактивной шине ChannelCognitiveBus.
/// </summary>
public enum CognitivePriority : byte
{
    BestEffort = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}