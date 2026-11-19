namespace Pickyfin.Server.Library;

/// <summary>One web search engine in the order set on the AI page.</summary>
public sealed class SearchEngineSetting
{
    public string Id { get; set; } = "";
    public bool On { get; set; }
    /// <summary>Searches allowed per <see cref="Period"/>; 0 = no limit by count.</summary>
    public int Allowance { get; set; }
    /// <summary>"day" or "month".</summary>
    public string Period { get; set; } = "month";
    /// <summary>Dollars a month; 0 = no limit by money. Only engines that cost something count.</summary>
    public double Budget { get; set; }
}

/// <summary>What Pickyfin knows about each engine: name, whether it needs a key, price per search.</summary>
public static class SearchEngines
{
    public sealed record Info(string Id, string Name, bool NeedsKey, string Note);

    public static readonly Info[] All =
    {
        new("parallel", "Parallel", false, "Free without a key (rate-limited, fast mode); with a key $0.001 fast or $0.005 advanced"),
        new("exa", "Exa", true, "About $0.007 a search; free plan $10 a month"),
        new("linkup", "Linkup", true, "$0.005 a search; free $20 a month (about 4,000 searches) with a work email"),
        new("tavily", "Tavily", true, "$0.008 a search; free 1,000 searches a month"),
        new("wikipedia", "Wikipedia", false, "Free, no key: the film's article (summary, plot, reception)"),
    };

    public static Info Get(string id) => All.FirstOrDefault(e => e.Id == id) ?? new Info(id, id, false, "");

    public static string? Key(ServerSettings s, string id) => id switch
    {
        "exa" => s.ExaApiKey, "parallel" => s.ParallelApiKey, "linkup" => s.LinkupApiKey, "tavily" => s.TavilyApiKey, _ => null,
    };

    public static bool HasKey(ServerSettings s, string id) => !string.IsNullOrWhiteSpace(Key(s, id));

    /// <summary>Can be used at all: switched on, and has a key if it needs one.</summary>
    public static bool Ready(ServerSettings s, SearchEngineSetting e) => e.On && (!Get(e.Id).NeedsKey || HasKey(s, e.Id));

    /// <summary>Costs nothing: Wikipedia, and Parallel without a key.</summary>
    public static bool Free(ServerSettings s, string id) => id == "wikipedia" || (id == "parallel" && !HasKey(s, id));

    /// <summary>The engine list, with every known engine once; built from the older Exa/Parallel settings the first time.</summary>
    public static List<SearchEngineSetting> For(ServerSettings s)
    {
        var list = s.SearchEngines.Where(e => All.Any(a => a.Id == e.Id)).GroupBy(e => e.Id).Select(g => g.First()).ToList();
        if (list.Count == 0)
        {
            var exa = new SearchEngineSetting { Id = "exa", On = (s.UseExa ?? s.WebSearchProvider == "exa") && HasKey(s, "exa"), Budget = s.WebLookupBudget };
            var parallel = new SearchEngineSetting { Id = "parallel", On = s.ParallelOn, Budget = s.ParallelBudget };
            list.AddRange(s.WebSearchOrder == "exa-first" ? new[] { exa, parallel } : new[] { parallel, exa });
        }
        foreach (var info in All.Where(a => list.All(e => e.Id != a.Id)))
            list.Add(info.Id switch
            {
                "linkup" => new SearchEngineSetting { Id = "linkup", Allowance = 4000, Period = "month" },
                "tavily" => new SearchEngineSetting { Id = "tavily", Allowance = 1000, Period = "month" },
                "wikipedia" => new SearchEngineSetting { Id = "wikipedia", On = true },
                _ => new SearchEngineSetting { Id = info.Id },
            });
        return list;
    }
}
