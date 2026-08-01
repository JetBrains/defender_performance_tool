using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using static WindowsDefenderPerformanceTool.MicrosoftAntimalwareEngineEvents;

namespace WindowsDefenderPerformanceTool;

public sealed class EtwListener : IDisposable
{
    // Unique per process: with TraceEventSession's default restart-on-create semantics a
    // fixed name makes concurrent instances stop each other's sessions, and two instances
    // racing through that stop/restart window make StartTrace fail outright — crashing the
    // app before its main window appears (observed when UI tests launched instances in parallel).
    private static readonly string SessionName =
        "WindowsDefenderPerformanceToolSession-" + Process.GetCurrentProcess().Id;

    private readonly TraceEventSession? _session;
    private readonly ETWTraceEventSource _source;
    private readonly Thread _processingThread;
    private bool _disposed;
    private long _rawCount;

    private readonly ConcurrentDictionary<Guid, (DateTime Timestamp, string Process, string FilePath)> _pendingStarts = new();

    // Orphaned Start events (Stop lost or never sent) are pruned after this age,
    // measured on the event-stream clock so live and replay behave the same.
    private static readonly TimeSpan PendingStartMaxAge = TimeSpan.FromMinutes(10);
    private const int EventsBetweenPrunes = 4096;
    private int _eventsSinceLastPrune;
    private DateTime _newestTimestamp = DateTime.MinValue;

    /// <summary>Raised on the processing thread for each matched Start/Stop pair.</summary>
    public event Action<EventInfo>? EventReceived;

    /// <summary>Raised when an ETL file has been fully replayed (file mode only).</summary>
    public event Action? Completed;

    /// <summary>Running total of raw ETW events received (all opcodes, not just matched pairs).</summary>
    public long RawEventCount => Interlocked.Read(ref _rawCount);

    /// <summary>Real-time ETW session mode. Requires administrator privileges.</summary>
    public EtwListener()
    {
        _session = new TraceEventSession(SessionName);
        _session.EnableProvider("Microsoft-Antimalware-Engine", TraceEventLevel.Informational);
        _source = _session.Source;
        _processingThread = CreateProcessingThread(completesOnReturn: false);
    }

    /// <summary>ETL file replay mode. Processes all events from the file, then raises <see cref="Completed"/>.</summary>
    public EtwListener(string etlFilePath)
    {
        _source = new ETWTraceEventSource(etlFilePath);
        _processingThread = CreateProcessingThread(completesOnReturn: true);
    }

    /// <summary>Starts processing events. Subscribe to <see cref="EventReceived"/> before calling this.</summary>
    public void Start() => _processingThread.Start();

    private Thread CreateProcessingThread(bool completesOnReturn)
    {
        _source.Dynamic.All += OnEvent;

        return new Thread(() =>
        {
            try
            {
                _source.Process();
            }
            catch (Exception)
            {
            }
            finally
            {
                if (completesOnReturn)
                    Completed?.Invoke();
            }
        })
        {
            IsBackground = true,
            Name = "ETW Processing Thread"
        };
    }

    private void OnEvent(TraceEvent data)
    {
        Interlocked.Increment(ref _rawCount);

        // OnEvent runs on the single Process() thread — plain fields are safe.
        if (data.TimeStamp > _newestTimestamp) _newestTimestamp = data.TimeStamp;
        if (++_eventsSinceLastPrune >= EventsBetweenPrunes)
        {
            _eventsSinceLastPrune = 0;
            PruneStalePendingStarts();
        }

        switch ((MicrosoftAntimalwareEngineEvents)data.ID)
        {
            case StreamscanrequestStart:
                try
                {
                    var process = Path.GetFileName(data.PayloadByName("Process")?.ToString() ?? "Unknown");
                    var filePath = data.PayloadByName("Path")?.ToString() ?? "";
                    _pendingStarts[data.ActivityID] = (data.TimeStamp, process, filePath);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }

                break;

            case StreamscanrequestStop:
                try
                {
                    if (!_pendingStarts.TryRemove(data.ActivityID, out var startInfo)) break;
                    var durationMsec = (data.TimeStamp - startInfo.Timestamp).TotalMilliseconds;
                    EventReceived?.Invoke(new EventInfo(durationMsec, startInfo.Process, data.TimeStamp,
                        startInfo.FilePath));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }

                break;
        }
    }

    private void PruneStalePendingStarts()
    {
        if (_pendingStarts.IsEmpty) return;
        var cutoff = _newestTimestamp - PendingStartMaxAge;
        foreach (var kvp in _pendingStarts)
        {
            if (kvp.Value.Timestamp < cutoff)
                _pendingStarts.TryRemove(kvp.Key, out _);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _source.Dynamic.All -= OnEvent;

        if (_session is not null)
        {
            _session.Stop();
            _session.Dispose();
        }
        else
        {
            _source.Dispose();
        }

        Completed?.Invoke();
    }
}
