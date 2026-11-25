namespace Pickyfin.Server.Ai;

/// <summary>
/// A manual run's scope: while it's set, every AI call underneath uses the model the admin
/// chose (not the model orders), may use paid models (it's apart from "Free only"), and
/// reports the tokens it used so the run can count what it costs.
/// It flows through awaits (AsyncLocal), so the steps' own code needs no extra parameters.
/// </summary>
public sealed class RunContext
{
    private static readonly AsyncLocal<RunContext?> Current = new();
    public static RunContext? Active => Current.Value;

    /// <summary>A manual run: every title described again gets its earlier tags and review in the prompt, not only flagged or looked-up ones.</summary>
    public bool EarlierTagsAlways { get; init; }

    /// <summary>The model for describing, checking and looking up in this scope.</summary>
    public string? ChatModel { get; init; }
    /// <summary>Called after each answer: model, input tokens, output tokens.</summary>
    public Action<string, int, int>? Used { get; init; }
    /// <summary>
    /// Asked before each request (model, estimated input and output tokens): false when it would go
    /// past the spending limit; the run's cancellation is then already set, so the call stops.
    /// </summary>
    public Func<string, int, int, bool>? Allow { get; init; }

    /// <summary>Stops the call when the run's limit says no (the token is the run's, already cancelled).</summary>
    public static void Check(string model, int input, int output, CancellationToken cancellationToken)
    {
        if (Active?.Allow is { } allow && !allow(model, input, output)) cancellationToken.ThrowIfCancellationRequested();
    }

    public static IDisposable Use(RunContext context)
    {
        var before = Current.Value;
        Current.Value = context;
        return new Restore(before);
    }

    private sealed class Restore(RunContext? before) : IDisposable
    {
        public void Dispose() => Current.Value = before;
    }
}
