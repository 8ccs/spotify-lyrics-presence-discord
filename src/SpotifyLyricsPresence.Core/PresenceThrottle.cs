namespace SpotifyLyricsPresence.Core;

/// <summary>What the Discord card should show. A null payload means "clear the activity".</summary>
/// <param name="StartUnix">Unix SECONDS (the Discord RPC docs example uses time(nullptr)); null = no timer.</param>
/// <param name="EndUnix">Unix seconds; together with a start Discord draws a progress bar.</param>
public sealed record PresencePayload(string Details, string State, long? StartUnix = null, long? EndUnix = null);

/// <summary>
/// Enforces a minimum spacing between Discord updates. Only the most recent desired state is
/// kept; superseded queued states are discarded, never replayed.
/// </summary>
public sealed class PresenceThrottle
{
    private readonly TimeSpan _minInterval;
    private DateTimeOffset _lastSend = DateTimeOffset.MinValue;
    private PresencePayload? _sent;
    private bool _hasSent;
    private PresencePayload? _pending;
    private bool _hasPending;

    public PresenceThrottle(TimeSpan minInterval) => _minInterval = minInterval;

    public bool HasPending => _hasPending;

    /// <summary>Declare the desired state (null = cleared). Supersedes any earlier pending state.</summary>
    public void Desire(PresencePayload? payload)
    {
        if (_hasSent && Equals(payload, _sent)) { _hasPending = false; _pending = null; return; }
        _pending = payload; _hasPending = true;
    }

    /// <summary>Returns the update to send now, if one is pending and spacing allows.</summary>
    public bool TryTake(DateTimeOffset now, out PresencePayload? payload)
    {
        payload = null;
        if (!_hasPending || now - _lastSend < _minInterval) return false;
        payload = _pending; _sent = payload; _hasSent = true;
        _pending = null; _hasPending = false; _lastSend = now;
        return true;
    }

    /// <summary>Time until the pending update is allowed, or null when nothing is pending.</summary>
    public TimeSpan? WaitTime(DateTimeOffset now)
    {
        if (!_hasPending) return null;
        var w = _lastSend + _minInterval - now;
        return w < TimeSpan.Zero ? TimeSpan.Zero : w;
    }

    /// <summary>Forget what Discord has (e.g. after reconnect) so the desired state is re-sent.</summary>
    public void Invalidate(PresencePayload? desired)
    {
        _hasSent = false; _sent = null; _pending = desired; _hasPending = true;
    }

    /// <summary>Record an out-of-band send (clearing on stop/quit bypasses spacing).</summary>
    public void MarkSent(DateTimeOffset now, PresencePayload? payload)
    {
        _sent = payload; _hasSent = true; _pending = null; _hasPending = false; _lastSend = now;
    }
}
