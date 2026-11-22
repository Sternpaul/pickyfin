namespace Pickyfin.Server.Data.Models;

/// <summary>How logs, jobs and pages name a set of titles: "12 films and 3 series", "1 film", "4 series".</summary>
public static class Titles
{
    public static string Count(int films, int series)
    {
        var f = $"{films:N0} film{(films == 1 ? "" : "s")}";
        var s = $"{series:N0} series";
        return films > 0 && series > 0 ? $"{f} and {s}" : series > 0 ? s : f;
    }

    public static string Count(IEnumerable<MovieMetadata> titles)
    {
        var list = titles as IReadOnlyCollection<MovieMetadata> ?? titles.ToList();
        var series = list.Count(m => m.IsSeries);
        return Count(list.Count - series, series);
    }
}
