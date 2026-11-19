namespace OpenFenestra.Core;

/// <summary>Monotonic milliseconds; only collapsed-to-expanded transitions arm the timer.</summary>
public sealed class IdleCollapse(bool initiallyCollapsed)
{
    private bool _collapsed = initiallyCollapsed;
    private long _lastActivity;
    public bool Armed { get; private set; }
    public void StateChanged(bool collapsed, bool enabled, long now)
    {
        if (collapsed || !enabled) Armed = false;
        else if (_collapsed) { Armed = true; Activity(now); }
        _collapsed = collapsed;
    }
    public void Activity(long now) => _lastActivity = now;
    public bool IsDue(long now, bool busy)
    {
        if (busy) { Activity(now); return false; }
        return Armed && now - _lastActivity >= 5000;
    }
}
