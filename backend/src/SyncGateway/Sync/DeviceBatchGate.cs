namespace Lagerkraft.SyncGateway.Sync;

/// <summary>Best-effort one-in-flight batch per device. Correctness is wms-core advisory lock.</summary>
public sealed class DeviceBatchGate
{
    private readonly HashSet<Guid> _inflight = [];
    private readonly object _lock = new();

    public bool TryEnter(Guid deviceId)
    {
        lock (_lock)
        {
            return _inflight.Add(deviceId);
        }
    }

    public void Exit(Guid deviceId)
    {
        lock (_lock)
        {
            _inflight.Remove(deviceId);
        }
    }
}
