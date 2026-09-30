namespace LabbyTwo.Core;

/// <summary>
/// One kind of container LabbyTwo recognises, and what it takes to reach it.
/// </summary>
/// <param name="Provider">The <see cref="IConnectionProvider.Type"/> it becomes. A provider that is
/// not installed — a plugin nobody added — is simply never offered.</param>
/// <param name="Port">The port the service listens on <em>inside</em> its container, which is the
/// one that matters on a shared network, and the one Docker's published-port list maps from.</param>
/// <param name="Scheme">"http" for nearly everything. The few images that serve only TLS inside
/// the container say "https", or the proposed URL would fail its first test for no good reason.</param>
/// <param name="Images">Repository patterns, lower case, without registry or tag:
/// <c>linuxserver/sonarr</c> covers <c>lscr.io/linuxserver/sonarr:4.0</c> and
/// <c>ghcr.io/linuxserver/sonarr@sha256:…</c> alike. <c>*</c> stands for one path segment, so
/// <c>*/sonarr</c> is anybody's Sonarr build but not <c>someone/sonarr-exporter</c>.</param>
/// <param name="Names">Container or Compose service names that identify it when the image cannot
/// — after a pull, Docker lists a container by bare image id until it is recreated.</param>
/// <param name="Note">Anything worth saying before the first test, beyond where the key is.</param>
public sealed record KnownService(
    string Provider,
    int Port,
    string Scheme,
    IReadOnlyList<string> Images,
    IReadOnlyList<string>? Names = null,
    string? Note = null);

/// <summary>
/// The table behind "add a connection for this": image patterns to provider, port and scheme.
///
/// Data rather than code for the same reason the providers describe their own fields — adding
/// a service is one line here and nothing anywhere else, and the README's list of what is
/// recognised can be checked against this and nothing more. Order matters only where two
/// entries could claim the same image; the first match wins, so the specific patterns come
/// before the <c>*/name</c> catch-alls.
///
/// A container can always overrule the table with labels — the same <c>labbytwo.*</c>
/// vocabulary the Docker labels import plugin reads, so one set of labels serves both:
/// <c>labbytwo.provider</c>, <c>labbytwo.url</c>, <c>labbytwo.port</c>, <c>labbytwo.name</c>,
/// <c>labbytwo.icon</c>, and <c>labbytwo.discover=false</c> (or <c>labbytwo.enable=false</c>)
/// to keep a container out of the suggestions altogether.
/// </summary>
public static class KnownServices
{
    public const string LabelPrefix = "labbytwo.";
    public const string ProviderLabel = LabelPrefix + "provider";
    public const string UrlLabel = LabelPrefix + "url";
    public const string PortLabel = LabelPrefix + "port";
    public const string NameLabel = LabelPrefix + "name";
    public const string IconLabel = LabelPrefix + "icon";
    public const string DiscoverLabel = LabelPrefix + "discover";
    public const string EnableLabel = LabelPrefix + "enable";

    private static KnownService S(string provider, int port, params string[] images) =>
        new(provider, port, "http", images, [provider]);

    public static readonly IReadOnlyList<KnownService> All =
    [
        // The *arrs. linuxserver and hotio are what nearly everybody runs; the catch-all
        // takes the rest without letting "sonarr-exporter" in.
        S("sonarr", 8989, "linuxserver/sonarr", "hotio/sonarr", "*/sonarr"),
        S("radarr", 7878, "linuxserver/radarr", "hotio/radarr", "*/radarr"),
        S("lidarr", 8686, "linuxserver/lidarr", "hotio/lidarr", "*/lidarr"),
        S("readarr", 8787, "linuxserver/readarr", "hotio/readarr", "*/readarr"),
        S("whisparr", 6969, "hotio/whisparr", "*/whisparr"),
        S("prowlarr", 9696, "linuxserver/prowlarr", "hotio/prowlarr", "*/prowlarr"),
        S("bazarr", 6767, "linuxserver/bazarr", "hotio/bazarr", "*/bazarr"),

        // Media servers and what hangs off them.
        new("plex", 32400, "http", ["plexinc/pms-docker", "linuxserver/plex", "hotio/plex", "*/plex"], ["plex"],
            "Plex is often run with network_mode: host — then it is on the host's own address, which is what is proposed."),
        S("jellyfin", 8096, "jellyfin/jellyfin", "linuxserver/jellyfin", "hotio/jellyfin", "*/jellyfin"),
        S("tautulli", 8181, "tautulli/tautulli", "linuxserver/tautulli", "hotio/tautulli", "*/tautulli"),
        new("seerr", 5055, "http",
            ["seerr-team/seerr", "sctx/overseerr", "fallenbagel/jellyseerr", "*/overseerr", "*/jellyseerr", "*/seerr"],
            ["seerr", "overseerr", "jellyseerr"]),
        S("ersatztv", 8409, "jasongdove/ersatztv", "*/ersatztv"),
        S("unmanic", 8888, "josh5/unmanic", "*/unmanic"),
        S("tdarr", 8265, "haveagitgat/tdarr", "*/tdarr"),
        S("audiobookshelf", 80, "advplyr/audiobookshelf", "*/audiobookshelf"),
        S("komga", 25600, "gotson/komga", "*/komga"),
        S("navidrome", 4533, "deluan/navidrome", "*/navidrome"),
        S("mylar3", 8090, "linuxserver/mylar3", "*/mylar3"),

        // Downloaders.
        new("qbittorrent", 8080, "http", ["linuxserver/qbittorrent", "hotio/qbittorrent", "*/qbittorrent", "*/qbittorrent-nox"],
            ["qbittorrent"]),
        S("transmission", 9091, "linuxserver/transmission", "*/transmission"),
        S("sabnzbd", 8080, "linuxserver/sabnzbd", "hotio/sabnzbd", "*/sabnzbd"),
        S("nzbget", 6789, "nzbgetcom/nzbget", "linuxserver/nzbget", "hotio/nzbget", "*/nzbget"),
        new("gluetun", 8000, "http", ["qmcgaw/gluetun", "*/gluetun"], ["gluetun"],
            "The control server listens on 8000 inside the container; recent Gluetun wants an API key or an auth config for it."),

        // Home and infrastructure.
        new("homeassistant", 8123, "http",
            ["home-assistant/home-assistant", "homeassistant/home-assistant", "linuxserver/homeassistant", "*/homeassistant"],
            ["homeassistant", "home-assistant"]),
        new("pihole", 80, "http", ["pihole/pihole"], ["pihole"]),
        new("adguard", 80, "http", ["adguard/adguardhome"], ["adguard", "adguardhome"],
            "AdGuard Home serves its first-run wizard on 3000 and moves to 80 once set up. If Test fails on 80, try 3000."),
        S("uptime-kuma", 3001, "louislam/uptime-kuma", "*/uptime-kuma"),
        S("syncthing", 8384, "syncthing/syncthing", "linuxserver/syncthing", "hotio/syncthing", "*/syncthing"),
        new("frigate", 5000, "http", ["blakeblackshear/frigate"], ["frigate"],
            "Port 5000 is Frigate's internal, unauthenticated API. Newer versions keep it off the host on purpose — a shared network is the way in."),
        new("immich", 2283, "http", ["immich-app/immich-server", "*/immich-server", "*/immich"], ["immich", "immich-server", "immich_server"]),
        new("nextcloud", 80, "http", ["nextcloud"], ["nextcloud"]),
        new("nextcloud", 443, "https", ["linuxserver/nextcloud"], ["nextcloud"],
            "linuxserver's Nextcloud serves HTTPS with its own certificate inside the container."),
        new("scrutiny", 8080, "http", ["analogj/scrutiny"], ["scrutiny"]),
        S("speedtest-tracker", 80, "linuxserver/speedtest-tracker", "*/speedtest-tracker"),
        S("duplicati", 8200, "duplicati/duplicati", "linuxserver/duplicati", "*/duplicati"),
        S("healthchecks", 8000, "healthchecks/healthchecks", "linuxserver/healthchecks"),
        S("gitea", 3000, "gitea/gitea", "*/gitea"),
        new("prometheus", 9090, "http", ["prom/prometheus", "*/prometheus"], ["prometheus"]),
        S("paperless", 8000, "paperless-ngx/paperless-ngx", "*/paperless-ngx"),
    ];

    /// <summary>
    /// The repository an image reference names, reduced to what the patterns are written in:
    /// lower case, no registry, no tag, no digest, and no <c>library/</c> for Docker Hub's
    /// official images — <c>docker.io/library/nextcloud:29</c> is <c>nextcloud</c>.
    /// </summary>
    public static string Repository(string? image)
    {
        var repository = ImageRef.Parse(image).Repository.ToLowerInvariant();
        return repository.StartsWith("library/", StringComparison.Ordinal) ? repository["library/".Length..] : repository;
    }

    /// <summary>Whether a repository fits one pattern, <c>*</c> standing for exactly one path segment.</summary>
    public static bool Fits(string repository, string pattern)
    {
        var have = repository.Split('/');
        var want = pattern.Split('/');
        if (have.Length != want.Length)
            return false;
        for (var i = 0; i < want.Length; i++)
        {
            if (want[i] != "*" && !string.Equals(want[i], have[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>The first entry whose patterns fit this image, or null.</summary>
    public static KnownService? ForImage(string? image)
    {
        var repository = Repository(image);
        if (repository.Length == 0)
            return null;
        return All.FirstOrDefault(service => service.Images.Any(pattern => Fits(repository, pattern)));
    }

    /// <summary>
    /// The first entry that goes by this name, for a container whose image Docker lists only
    /// as an id. Exact names only: guessing from "sonarr-anime" is the image's job.
    /// </summary>
    public static KnownService? ForName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : All.FirstOrDefault(service => service.Names?.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase) == true);

    /// <summary>The first entry for a provider, for a container labelled with one.</summary>
    public static KnownService? ForProvider(string? provider) =>
        All.FirstOrDefault(service => string.Equals(service.Provider, provider, StringComparison.OrdinalIgnoreCase));
}
