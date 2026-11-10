namespace Pickyfin.Server;

/// <summary>What the server is waiting on right now, for the status line ("Waiting for the AI · 0:42").</summary>
public static class Activity
{
    private static readonly object Gate = new();
    private static string? _what;
    private static DateTime _since;

    public static IDisposable Begin(string what)
    {
        lock (Gate) { _what = what; _since = DateTime.UtcNow; }
        return new End(what);
    }

    public static (string? What, int Seconds) Current
    {
        get { lock (Gate) return (_what, _what is null ? 0 : (int)(DateTime.UtcNow - _since).TotalSeconds); }
    }

    private sealed class End(string what) : IDisposable
    {
        public void Dispose() { lock (Gate) if (_what == what) _what = null; }
    }
}
