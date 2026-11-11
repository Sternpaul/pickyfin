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

    /// <summary>Which model and which try, while waiting for the AI ("model 2 of 4: gemma…, try 1 of 3").</summary>
    public static string? Detail { get; set; }

    /// <summary>A long run in the background ("Looking up flagged movies: 20 of 85"), shown between its waits.</summary>
    public static string? Background { get; set; }

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

/// <summary>
/// What each background job is doing, for the Status page: running now, waiting until a
/// set time (and why), finished, or switched off. One line per job, always current.
/// </summary>
public static class Jobs
{
    public sealed record Job(string Key, string Title, string Now, bool Running, bool Error, DateTime? Next, DateTime Since);

    private static readonly string[] Order = { "library", "tagging", "second", "lookupall", "sweep", "picks" };
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Job> All = new();

    public static void Running(string key, string title, string now)
    {
        lock (Gate)
        {
            var since = All.TryGetValue(key, out var was) && was.Running ? was.Since : DateTime.UtcNow;
            All[key] = new Job(key, title, now, true, false, null, since);
        }
    }

    public static void Idle(string key, string title, string now, DateTime? next = null, bool error = false)
    {
        lock (Gate) All[key] = new Job(key, title, now, false, error, next, DateTime.UtcNow);
    }

    /// <summary>A line for a job that hasn't run yet, so Status always lists every job.</summary>
    public static void Ensure(string key, string title, string now)
    {
        lock (Gate) All.TryAdd(key, new Job(key, title, now, false, false, null, DateTime.UtcNow));
    }

    public static Job? Get(string key)
    {
        lock (Gate) return All.GetValueOrDefault(key);
    }

    public static List<Job> Snapshot()
    {
        lock (Gate) return All.Values.OrderBy(j => Array.IndexOf(Order, j.Key) is var i && i < 0 ? 99 : i).ToList();
    }

    /// <summary>"09:10 (in 23 min)" in the server's local time, for log lines.</summary>
    public static string When(DateTime utc)
    {
        var minutes = Math.Max(0, (int)Math.Round((utc - DateTime.UtcNow).TotalMinutes));
        return $"{utc.ToLocalTime():HH:mm} (in {minutes} min)";
    }
}
