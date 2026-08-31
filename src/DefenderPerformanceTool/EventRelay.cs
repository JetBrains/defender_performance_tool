using System;

namespace DefenderPerformanceTool;

/// <summary>
/// Central relay: both live and file listeners feed scan events into this, and
/// subscribers (plotter, stats batching) attach here. Replaces System.Reactive's Subject.
/// Handlers run on the publishing (ETW processing) thread — they must be thread-safe.
/// </summary>
public sealed class EventRelay
{
    public event Action<EventInfo>? Received;

    public void Publish(EventInfo e) => Received?.Invoke(e);
}
