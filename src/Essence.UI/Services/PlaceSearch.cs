using System.Globalization;
using System.Text;
using Essence.Contracts;

namespace Essence.UI.Services;

/// <summary>
/// The address search behind "Going somewhere?": what's typed, from 3 characters, goes to the API 300 ms after the last
/// keystroke, and a newer search cancels the one on its way. Answers are kept by what was typed, so going back to it (a
/// letter deleted) shows them at once; while a longer text is on its way, the answers for the longest one it starts with
/// show, cut to those that still match.
/// </summary>
public sealed class PlaceSearch(EssenceApiClient api, TimeProvider time) : IDisposable
{
    public const int MinLength = 3;
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    /// <summary>Answers kept, the oldest forgotten first.</summary>
    private const int CacheSize = 50;

    private readonly Dictionary<string, IReadOnlyList<PlaceSuggestion>> _cache = [];
    private readonly Queue<string> _cached = new();
    private CancellationTokenSource? _cts;

    /// <summary>Raised when the suggestions, the progress or the error changed.</summary>
    public event Action? Changed;

    /// <summary>What was typed, as the user typed it.</summary>
    public string Text { get; private set; } = "";

    /// <summary>
    /// The suggestions for <see cref="Text"/>; while they're on their way, those for the longest text it starts with that
    /// still match. Empty under <see cref="MinLength"/> characters.
    /// </summary>
    public IReadOnlyList<PlaceSuggestion> Suggestions { get; private set; } = [];

    /// <summary>The suggestions shown are <see cref="Text"/>'s own (not a shorter text's, cut down).</summary>
    public bool Answered { get; private set; }

    public bool Loading { get; private set; }

    /// <summary>Why the last search failed: <see cref="ApiError.Unavailable"/> when the address search is down.</summary>
    public ApiError Error { get; private set; }

    /// <summary>Enough was typed to search.</summary>
    public bool Searching => Key(Text).Length >= MinLength;

    /// <summary>The text changed: searches it once typing pauses, near <paramref name="near"/> (the user, or the map's centre).</summary>
    /// <param name="language"><c>fr</c> or <c>en</c>.</param>
    public void Type(string text, GeoPoint? near, string language)
    {
        _cts?.Cancel();
        _cts = null;
        Text = text;
        var key = Key(text);
        if (key.Length < MinLength)
        {
            (Suggestions, Answered, Loading, Error) = ([], false, false, ApiError.None);
            Changed?.Invoke();
            return;
        }

        var cacheKey = CacheKey(key, near, language);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            (Suggestions, Answered, Loading, Error) = (cached, true, false, ApiError.None);
            Changed?.Invoke();
            return;
        }

        (Suggestions, Answered, Loading) = (FromPrefix(key, near, language), false, true);
        Changed?.Invoke();
        var cts = _cts = new CancellationTokenSource();
        _ = SearchLaterAsync(key, cacheKey, near, language, cts);
    }

    /// <summary>Clears the text and what was found for it; the answers kept stay.</summary>
    public void Clear() => Type("", null, "fr");

    private async Task SearchLaterAsync(string key, string cacheKey, GeoPoint? near, string language, CancellationTokenSource cts)
    {
        ApiResult<List<PlaceSuggestion>> result;
        try
        {
            await Task.Delay(Debounce, time, cts.Token);
            result = await api.SearchPlacesAsync(key, near is { } point ? Rounded(point) : null, language, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        Loading = false;
        Error = result.Error;
        if (result.Value is { } suggestions)
        {
            Remember(cacheKey, suggestions);
            (Suggestions, Answered) = (suggestions, true);
        }
        else
        {
            // What a shorter text found may not be there: nothing rather than something wrong.
            (Suggestions, Answered) = ([], false);
        }

        Changed?.Invoke();
    }

    /// <summary>The answers kept for the longest text this one starts with, cut to those every word typed still matches.</summary>
    private IReadOnlyList<PlaceSuggestion> FromPrefix(string key, GeoPoint? near, string language)
    {
        for (var length = key.Length - 1; length >= MinLength; length--)
        {
            if (_cache.TryGetValue(CacheKey(key[..length], near, language), out var shorter))
            {
                return [.. shorter.Where(s => Matches(s, key))];
            }
        }

        return [];
    }

    private void Remember(string cacheKey, IReadOnlyList<PlaceSuggestion> suggestions)
    {
        if (_cache.TryAdd(cacheKey, suggestions))
        {
            _cached.Enqueue(cacheKey);
            if (_cached.Count > CacheSize)
            {
                _cache.Remove(_cached.Dequeue());
            }
        }
    }

    /// <summary>Every word typed starts a word of the suggestion (the last one may be cut short: "gauch").</summary>
    public static bool Matches(PlaceSuggestion suggestion, string typed)
    {
        var words = Fold($"{suggestion.Label} {suggestion.Detail}").Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        return Fold(typed).Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .All(t => words.Any(w => w.StartsWith(t, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Where in <paramref name="label"/> the words typed match the starts of its words, as (start, length) spans in order,
    /// for showing them in bold. Case and accents don't count ("gauch" matches "Gauchetière").
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> Highlights(string label, string typed)
    {
        var folded = FoldKeepingLength(label);
        var terms = Fold(typed).Split(Separators, StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
        var spans = new List<(int Start, int Length)>();
        for (var i = 0; i < folded.Length; i++)
        {
            if (char.IsLetterOrDigit(folded[i]) && (i == 0 || !char.IsLetterOrDigit(folded[i - 1])))
            {
                var longest = terms.Where(t => string.CompareOrdinal(folded, i, t, 0, t.Length) == 0).Select(t => t.Length).DefaultIfEmpty(0).Max();
                if (longest > 0)
                {
                    spans.Add((i, longest));
                    i += longest - 1;
                }
            }
        }

        return spans;
    }

    public void Dispose() => _cts?.Cancel();

    private static readonly char[] Separators = [' ', ',', '-', '\'', '’', '.', '(', ')'];

    /// <summary>What's searched: trimmed, the spaces collapsed.</summary>
    private static string Key(string text) => string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Kept per language and per area of about 10 km: another place to start from ranks the places differently.</summary>
    private static string CacheKey(string key, GeoPoint? near, string language) =>
        $"{language}|{(near is { } p ? string.Create(CultureInfo.InvariantCulture, $"{p.Latitude:0.0},{p.Longitude:0.0}") : "-")}|{Fold(key)}";

    /// <summary>To about 10 m: closer to the user doesn't change what's found.</summary>
    private static GeoPoint Rounded(GeoPoint point) => new(Math.Round(point.Latitude, 4), Math.Round(point.Longitude, 4));

    private static string Fold(string text) => FoldKeepingLength(text);

    /// <summary>
    /// Lower case without accents, one character for one, so positions in it are positions in the text. Half of a
    /// surrogate pair (an emoji) is kept as is: it can't be normalized alone.
    /// </summary>
    private static string FoldKeepingLength(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsSurrogate(c))
            {
                builder.Append(c);
                continue;
            }

            var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
            builder.Append(char.ToLowerInvariant(decomposed.Length > 0 ? decomposed[0] : c));
        }

        return builder.ToString();
    }
}

/// <summary>What a place found in the search is for.</summary>
public enum PlacePurpose
{
    /// <summary>A trip there (Best on the way).</summary>
    Destination,
    Home,
    Work,

    /// <summary>Another saved place, named once it's found.</summary>
    Other,
}
