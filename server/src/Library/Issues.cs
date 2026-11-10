using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Library;

/// <summary>Why a movie's tags may be wrong, as lines for the Issues page.</summary>
public static class Issues
{
    public const string Unknown = "The AI didn't know this film and went only by the plot";
    public const string Unsure = "The AI wasn't sure about this film";

    /// <param name="known">"yes", "partly" or "no", as the AI said.</param>
    public static string? Describe(MovieMetadata movie, string known)
    {
        var lines = new List<string>();
        if (!movie.IsClassified) lines.Add("The AI gave no usable subcategory");
        // With web text in hand, not knowing the film is fine
        var lookedUp = !string.IsNullOrEmpty(movie.WebNotes);
        if (known == "no" && !lookedUp) lines.Add(Unknown);
        if (known == "partly" && !lookedUp) lines.Add(Unsure);
        // The genre check only guards against the AI's memory; a web lookup beats Jellyfin's coarse genres
        if (!lookedUp) lines.AddRange(GenreCheck.Problems(movie));
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    public static string? Add(string? issue, string line) =>
        string.IsNullOrEmpty(issue) ? line : issue.Split('\n').Contains(line) ? issue : issue + "\n" + line;
}
