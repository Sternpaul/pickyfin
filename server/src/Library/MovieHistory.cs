using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pickyfin.Server.Ai;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Library;

/// <summary>One thing that happened to a film's tags: who did it, with which model, what changed and what was said.</summary>
public sealed class HistoryEntry
{
    public int Id { get; set; }
    public Guid ItemId { get; set; }
    public DateTime At { get; set; }
    /// <summary>describe, check, genre, web, redescribe, recheck, you</summary>
    public string Step { get; set; } = "";
    public string? Model { get; set; }
    /// <summary>Tags before and after, as JSON {"S":[…],"M":[…],"T":[…]}; null when there were none.</summary>
    public string? Before { get; set; }
    public string? After { get; set; }
    public string? Text { get; set; }
}

/// <summary>
/// Keeps the story of each film's tags, for the History part of its page: the first description,
/// what the second opinion and the genre check said, what Exa found, how it was described again,
/// and what you changed. The last 60 entries per film are kept.
/// </summary>
public sealed class MovieHistory
{
    private const int KeepPerFilm = 60;
    private readonly Database _database;
    private readonly ILogger<MovieHistory> _logger;

    public MovieHistory(Database database, ILogger<MovieHistory> logger)
    {
        _database = database;
        _logger = logger;
    }

    public static string? Tags(MovieMetadata m) => !m.IsClassified ? null : JsonSerializer.Serialize(new
    {
        S = m.SubcategorySet.Items, M = m.MoodSet.Items, T = m.ThemeSet.Items,
    });

    /// <summary>The model that answered last for a step ("describe", "check", "lookup"), else the configured one.</summary>
    public static string? ModelFor(string step)
    {
        var s = ServerSettings.Current;
        if (s.AIProvider == AIProviderType.OpenRouter && OpenRouterModels.LastAnswered.TryGetValue(step, out var last)) return last.Model;
        return step == "check" ? s.CheckModel : s.ClassificationModel;
    }

    public static HistoryEntry Entry(MovieMetadata m, string step, string? model, string? before, string? text) => new()
    {
        ItemId = m.ItemId, At = DateTime.UtcNow, Step = step, Model = model, Before = before, After = Tags(m), Text = text,
    };

    /// <summary>Writes entries; a failure here never stops the work it describes.</summary>
    public async Task AddAsync(IEnumerable<HistoryEntry> entries, CancellationToken cancellationToken)
    {
        var list = entries.ToList();
        if (list.Count == 0) return;
        try
        {
            await using var db = _database.Open();
            db.History.AddRange(list);
            await db.SaveChangesAsync(cancellationToken);
            foreach (var id in list.Select(e => e.ItemId).Distinct())
            {
                var old = await db.History.Where(h => h.ItemId == id).OrderByDescending(h => h.At).Skip(KeepPerFilm).Select(h => h.Id).ToListAsync(cancellationToken);
                if (old.Count > 0) await db.History.Where(h => old.Contains(h.Id)).ExecuteDeleteAsync(cancellationToken);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning("Couldn't write film history: {Error}", error.Message);
        }
    }

    public Task AddAsync(HistoryEntry entry, CancellationToken cancellationToken) => AddAsync(new[] { entry }, cancellationToken);

    private static readonly Dictionary<string, string[]> StepEntries = new()
    {
        ["describe"] = new[] { "describe", "describe-again" },
        ["check"] = new[] { "check" },
        ["lookup"] = new[] { "redescribe" },
        ["rescan"] = new[] { "recheck" },
    };
    private (DateTime At, Dictionary<string, List<(string Model, int Films)>> Counts)? _modelCounts;

    /// <summary>Per step, how many films each model handled last (from the history kept so far).</summary>
    public async Task<Dictionary<string, List<(string Model, int Films)>>> ModelsByStepAsync(CancellationToken cancellationToken)
    {
        if (_modelCounts is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(1)) return cached.Counts;
        var all = StepEntries.Values.SelectMany(v => v).ToArray();
        List<(Guid ItemId, string Step, string? Model, DateTime At)> rows;
        await using (var db = _database.Open())
            rows = (await db.History.AsNoTracking().Where(h => all.Contains(h.Step))
                .Select(h => new { h.ItemId, h.Step, h.Model, h.At }).ToListAsync(cancellationToken))
                .Select(h => (h.ItemId, h.Step, h.Model, h.At)).ToList();
        var counts = StepEntries.ToDictionary(e => e.Key, e => rows
            .Where(r => e.Value.Contains(r.Step))
            .GroupBy(r => r.ItemId).Select(g => g.OrderByDescending(r => r.At).First().Model ?? "unknown")
            .GroupBy(m => m).Select(g => (Model: g.Key, Films: g.Count())).OrderByDescending(x => x.Films).ToList());
        _modelCounts = (DateTime.UtcNow, counts);
        return counts;
    }

    public async Task<List<HistoryEntry>> ForAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await using var db = _database.Open();
        return await db.History.AsNoTracking().Where(h => h.ItemId == itemId).OrderBy(h => h.At).ThenBy(h => h.Id).ToListAsync(cancellationToken);
    }
}
