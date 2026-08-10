namespace LabbyTwo.Core;

/// <summary>
/// Routes the server answers itself, outside the Blazor circuit. This exists because a
/// component cannot hand the browser bytes: a download that honours Range headers so a
/// video seeks, an upload that is not squeezed through the SignalR circuit, a share link
/// somebody opens on a phone. Everything else about it matches the other extension
/// points — a public class, found by scanning, in the app or in a plugin DLL.
/// </summary>
public interface IEndpointExtension
{
    /// <summary>
    /// Stable key, and the URL segment the routes live under. Never change it: a link
    /// somebody bookmarked contains it.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// False only for routes that must answer without a login on an install that has
    /// one — a share link is the reason this is here. It defaults to true because the
    /// safe answer has to be the one you get by not thinking about it.
    /// </summary>
    bool RequiresAuthorization => true;

    /// <summary>
    /// Maps the routes. Paths are relative to the group, so <c>"/download"</c> here
    /// answers at <c>/ext/your-key/download</c>. Called once at startup; throwing takes
    /// this extension out and reports it on the Settings page rather than stopping the
    /// app, which is the same bargain a plugin that fails to load already gets.
    /// </summary>
    void Map(IEndpointRouteBuilder routes);
}

/// <summary>
/// Where an extension's routes live, so the host and the plugin building links agree on
/// it without either hardcoding the other's string.
/// </summary>
public static class ExtensionRoutes
{
    public const string Prefix = "/ext";

    public static string PathFor(string key) => $"{Prefix}/{key}";

    /// <summary>
    /// Keys are restricted rather than escaped. A key containing a slash or a route
    /// brace would quietly claim URLs its author never wrote — including, with enough
    /// dots, ones the app needs — so a bad key means "not mapped, and said out loud".
    /// </summary>
    public static bool IsValidKey(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
