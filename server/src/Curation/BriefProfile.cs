using System.Text.Json;
using System.Text.RegularExpressions;
using Pickyfin.Server.Ai;
using Pickyfin.Server.Library;

namespace Pickyfin.Server.Curation;

/// <summary>A brief, translated into the same fixed vocabulary the movies are tagged with.</summary>
public sealed class BriefProfile
{
    public List<string> Subcategories { get; set; } = new();
    public List<string> Moods { get; set; } = new();
    public List<string> Themes { get; set; } = new();
    public List<string> Keywords { get; set; } = new();
    public List<string> AvoidSubcategories { get; set; } = new();
    public List<string> AvoidMoods { get; set; } = new();

    public bool IsEmpty => Subcategories.Count == 0 && Moods.Count == 0 && Themes.Count == 0 && Keywords.Count == 0;

    public static BriefProfile? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<BriefProfile>(json); }
        catch (JsonException) { return null; }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}

/// <summary>Asks the AI once per brief change to map the admin's words onto the vocabulary.</summary>
public sealed class BriefInterpreter
{
    private readonly AIProviderFactory _ai;
    private readonly ILogger<BriefInterpreter> _logger;

    public BriefInterpreter(AIProviderFactory ai, ILogger<BriefInterpreter> logger)
    {
        _ai = ai;
        _logger = logger;
    }

    private static string Quoted(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"\"{v}\""));

    public async Task<BriefProfile> InterpretAsync(string brief, CancellationToken cancellationToken)
    {
        var system = "You translate a short description of a person's movie taste into tags for a recommendation engine. Output only JSON.";
        var prompt = $@"Description of the viewer, written by the person who runs the media server:
""""""
{brief}
""""""

Return a JSON object with:
- ""subcategories"": 3 to 8 values they would enjoy, ONLY from: {Quoted(ClassificationVocabulary.Subcategories)}
- ""moods"": 2 to 5 values they would enjoy, ONLY from: {Quoted(ClassificationVocabulary.Moods)}
- ""themes"": 3 to 8 short lowercase phrases describing subjects they like (free text)
- ""keywords"": up to 10 lowercase TMDB-style keywords (e.g. ""heist"", ""serial killer"", ""time travel"")
- ""avoidSubcategories"": values from the same subcategory list they clearly dislike (may be empty)
- ""avoidMoods"": values from the same mood list they clearly dislike (may be empty)

Output ONLY the JSON object.";

        var response = await _ai.GetProvider().ChatAsync(prompt, system, cancellationToken);
        var json = ExtractJson(response) ?? throw new InvalidOperationException("The AI did not return JSON for the brief.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var profile = new BriefProfile
        {
            Subcategories = Canonical(Strings(root, "subcategories"), ClassificationVocabulary.CanonicalSubcategory),
            Moods = Canonical(Strings(root, "moods"), ClassificationVocabulary.CanonicalMood),
            Themes = Strings(root, "themes").Select(t => t.ToLowerInvariant()).Distinct().ToList(),
            Keywords = Strings(root, "keywords").Select(t => t.ToLowerInvariant()).Distinct().ToList(),
            AvoidSubcategories = Canonical(Strings(root, "avoidSubcategories"), ClassificationVocabulary.CanonicalSubcategory),
            AvoidMoods = Canonical(Strings(root, "avoidMoods"), ClassificationVocabulary.CanonicalMood),
        };
        _logger.LogInformation("Brief understood as: {Subcategories} / moods {Moods}", string.Join(", ", profile.Subcategories), string.Join(", ", profile.Moods));
        return profile;
    }

    private static List<string> Canonical(IEnumerable<string> raw, Func<string?, string?> canonical) =>
        raw.Select(canonical).Where(v => v is not null).Select(v => v!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static IEnumerable<string> Strings(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var value in property.Value.EnumerateArray())
                if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                    yield return value.GetString()!.Trim();
        }
    }

    /// <summary>Finds the JSON object in a model's answer, with or without a code fence.</summary>
    public static string? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var fenced = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)\s*```");
        if (fenced.Success) text = fenced.Groups[1].Value;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }
}
