namespace LabbyTwo.Core;

/// <summary>
/// Where the search box sends you. Each engine is a URL plus the name of its query
/// parameter, which is all a GET form needs — so adding an engine is one line and no code
/// runs on the server when somebody searches.
/// </summary>
/// <param name="Key">Stored on the widget.</param>
/// <param name="QueryParameter">The form field name the engine expects, usually "q".</param>
/// <param name="ExtraParameters">Fixed values the engine needs alongside the query.</param>
public sealed record SearchEngine(
    string Key,
    string DisplayName,
    string Url,
    string QueryParameter = "q",
    string Icon = "🔍",
    IReadOnlyDictionary<string, string>? Extra = null)
{
    public IReadOnlyDictionary<string, string> ExtraParameters => Extra ?? new Dictionary<string, string>();

    public const string CustomKey = "custom";

    public static readonly IReadOnlyList<SearchEngine> All =
    [
        new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/", Icon: "🦆"),
        new("google", "Google", "https://www.google.com/search"),
        new("bing", "Bing", "https://www.bing.com/search"),
        new("brave", "Brave", "https://search.brave.com/search", Icon: "🦁"),
        new("startpage", "Startpage", "https://www.startpage.com/sp/search"),
        new("kagi", "Kagi", "https://kagi.com/search"),
        new("ecosia", "Ecosia", "https://www.ecosia.org/search", Icon: "🌱"),
        new("wikipedia", "Wikipedia", "https://en.wikipedia.org/w/index.php", "search", "📖"),
        new("youtube", "YouTube", "https://www.youtube.com/results", "search_query", "▶️"),
        new("github", "GitHub", "https://github.com/search", Icon: "🐙", Extra: new Dictionary<string, string> { ["type"] = "repositories" }),
        new("perplexity", "Perplexity", "https://www.perplexity.ai/search", Icon: "🤖"),
    ];

    public static IReadOnlyList<SelectOption> Options =>
    [
        .. All.Select(e => new SelectOption(e.Key, e.DisplayName)),
        new SelectOption(CustomKey, "Custom — your own URL"),
    ];

    /// <summary>
    /// Resolves a stored key, falling back to DuckDuckGo. "custom" points at whatever the
    /// user typed, which is how a self-hosted SearXNG or a LAN wiki becomes the default
    /// search without an entry in the list above.
    /// </summary>
    public static SearchEngine Resolve(string key, string? customUrl)
    {
        if (string.Equals(key, CustomKey, StringComparison.OrdinalIgnoreCase))
        {
            var url = string.IsNullOrWhiteSpace(customUrl) ? "https://duckduckgo.com/" : customUrl.Trim();

            // "http://searx.lan/search?q=" is how people naturally write a search URL, so
            // accept it and take the parameter name from the query string.
            var separator = url.IndexOf('?');
            if (separator > 0)
            {
                var query = url[(separator + 1)..].TrimEnd('=');
                var parameter = query.Split('&')[^1].Split('=')[0];
                if (parameter.Length > 0)
                    return new SearchEngine(CustomKey, "search", url[..separator], parameter);
            }

            return new SearchEngine(CustomKey, "search", url);
        }

        return All.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) ?? All[0];
    }
}
