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

/// <summary>Rough time left for the tagging run, from how long the last AI calls took.</summary>
public static class TaggingEta
{
    private static readonly object Gate = new();
    private static readonly Queue<double> Recent = new();
    private static int _remaining; // movies not yet sent, including the batch in flight
    private static int _batchSize = 1;
    private static DateTime _batchStarted;
    private static bool _running;

    public static void Start(int total, int batchSize)
    {
        lock (Gate) { Recent.Clear(); _remaining = total; _batchSize = Math.Max(1, batchSize); _running = true; }
    }

    public static void BatchStarted(int remaining)
    {
        lock (Gate) { _remaining = remaining; _batchStarted = DateTime.UtcNow; }
    }

    public static void BatchDone()
    {
        lock (Gate)
        {
            Recent.Enqueue((DateTime.UtcNow - _batchStarted).TotalSeconds);
            while (Recent.Count > 8) Recent.Dequeue();
        }
    }

    public static void Stop()
    {
        lock (Gate) { _running = false; Recent.Clear(); }
    }

    /// <summary>Seconds left, or null while there's nothing to go on yet.</summary>
    public static int? SecondsLeft
    {
        get
        {
            lock (Gate)
            {
                if (!_running || Recent.Count == 0) return null;
                var perBatch = Recent.Average();
                var batches = (int)Math.Ceiling(_remaining / (double)_batchSize);
                var left = batches * perBatch - (DateTime.UtcNow - _batchStarted).TotalSeconds;
                return (int)Math.Max(perBatch * 0.1, left);
            }
        }
    }
}
