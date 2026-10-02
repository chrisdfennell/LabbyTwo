using System.Text.Json;
using System.Text.RegularExpressions;

namespace LabbyTwo.Core;

/// <summary>How much a plain-text secret matters.</summary>
public enum SecretSeverity
{
    /// <summary>A name that suggests a secret, holding something that does not look like much of one.</summary>
    Low = 1,

    /// <summary>Password-like: a password, a URL or connection string with one in it, a placeholder.</summary>
    Medium = 2,

    /// <summary>A live credential or token: a VPN login, a GitHub token, a random API key.</summary>
    High = 3,
}

/// <summary>Where in a container's settings a secret was found.</summary>
public static class SecretPlaces
{
    public const string Env = "environment";
    public const string Command = "command";
    public const string Label = "label";
}

/// <summary>
/// One plain-text secret in one container's settings — described, never quoted. There is
/// deliberately no field that holds the value or anything derived from it beyond its
/// length: this record is what the page draws, what the log and the change feed are told,
/// and what a test serialises to prove that, so if the value is not in here it cannot leak
/// from any of them.
/// </summary>
/// <param name="Container">The container's name.</param>
/// <param name="Where">One of <see cref="SecretPlaces"/>.</param>
/// <param name="Name">The variable, label or flag: <c>OPENVPN_PASSWORD</c>, <c>--api-key</c>.</param>
/// <param name="Kind">What it looks like, in words: "VPN password", "GitHub token".</param>
/// <param name="Severity">How much it matters.</param>
/// <param name="Length">How long the value is — the only thing said about it, enough to tell
/// two findings with the same name apart without saying anything useful about either.</param>
public sealed record SecretFinding(
    string Container,
    string Where,
    string Name,
    string Kind,
    SecretSeverity Severity,
    int Length)
{
    /// <summary>A placeholder or a default ("changeme", "password") — guessable as well as visible.</summary>
    public bool Weak { get; init; }

    /// <summary>The image it runs, for advice that depends on it (linuxserver's <c>FILE__</c>).</summary>
    public string Image { get; init; } = "";

    /// <summary>
    /// Other findings holding the very same value, as "container NAME" — one password used in
    /// three places is three places to change it. Worked out while the values were in hand
    /// and only the names kept.
    /// </summary>
    public IReadOnlyList<string> SameValueAs { get; init; } = [];

    /// <summary>Marked as handled by somebody, so left out of the counts.</summary>
    public bool Ignored { get; init; }

    /// <summary>What the ignore list remembers it by: the container and the name, not the value.</summary>
    public string Key => SecretScan.IgnoreKey(Container, Name);

    /// <summary>"24 characters".</summary>
    public string Fingerprint => Length == 1 ? "1 character" : $"{Length} characters";

    public string SeverityText => Severity switch
    {
        SecretSeverity.High => "high",
        SecretSeverity.Medium => "medium",
        _ => "low",
    };

    /// <summary>What to do about it, in a few short sentences.</summary>
    public IReadOnlyList<string> Advice => SecretScan.Advise(this);
}

/// <summary>
/// What one container was given, as read from <c>docker inspect</c> — values and all.
/// Exists only for the length of a check: <see cref="SecretScan.FindAll"/> turns it into
/// findings, which carry no values, and it is dropped. Not a record, so it has no
/// generated <c>ToString</c> printing every value into whatever log line it ends up in.
/// </summary>
public sealed class SecretInput
{
    public string Container { get; init; } = "";
    public string Image { get; init; } = "";
    public IReadOnlyList<KeyValuePair<string, string>> Env { get; init; } = [];
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyList<KeyValuePair<string, string>> Labels { get; init; } = [];

    public override string ToString() => $"settings of {Container}";
}

/// <summary>
/// Finding passwords, tokens and keys written in plain text into containers' settings —
/// the environment, the command and the labels — where anybody who can read the Docker API,
/// or who is sent the compose file, can read them too. The prompt for it was a compose file
/// pasted into a chat with a VPN login in it as <c>OPENVPN_USER</c> and
/// <c>OPENVPN_PASSWORD</c>.
///
/// Pure, so every rule is a line in a test table rather than a container. Three ways in,
/// in order:
/// <list type="number">
/// <item><b>The value's shape</b>, whatever it is called: a GitHub, Slack, AWS, Tailscale
/// or Telegram token, a Discord or Slack webhook, a JWT, a private key, a URL or connection
/// string with a password in it. These are certain enough to be worth saying even under a
/// name like <c>FOO</c>.</item>
/// <item><b>Names that belong to one product</b> — gluetun's <c>OPENVPN_PASSWORD</c>,
/// <c>WIREGUARD_PRIVATE_KEY</c>, cloudflared's <c>TUNNEL_TOKEN</c> — whose meaning is
/// known exactly.</item>
/// <item><b>Names with a secret word in them</b> — PASSWORD, TOKEN, SECRET, KEY, AUTH… —
/// graded by what they hold: a long random value is a live credential, a short word under
/// <c>AUTH</c> is probably a setting ("basic").</item>
/// </list>
///
/// What is left alone matters as much, or the list is noise nobody reads: values that point
/// at a file (<c>*_FILE=/run/secrets/…</c>, linuxserver's <c>FILE__VAR=</c>), names that
/// describe a secret rather than hold one (<c>PASSWORD_FILE</c>, <c>TOKEN_TTL</c>,
/// <c>AUTH_METHOD</c>), public keys and image checksums (<c>GPG_KEY</c>, which the official
/// Python image sets), and the usual settings (<c>PUID</c>, <c>TZ</c>, <c>UMASK</c>).
///
/// Wider than <see cref="ContainerConfigs.SecretWords"/>, which errs the other way on
/// purpose: hiding a value that did not need hiding costs nothing, but telling somebody to
/// rotate something that is not a secret costs their trust in the rest of the list.
/// </summary>
public static partial class SecretScan
{
    // ---- reading ---------------------------------------------------------------------

    /// <summary>The parts of an inspect payload a secret can hide in.</summary>
    public static SecretInput Read(string inspect)
    {
        using var document = JsonDocument.Parse(inspect);
        var root = document.RootElement;
        var config = root.TryGetProperty("Config", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;

        var env = new List<KeyValuePair<string, string>>();
        foreach (var line in Strings(config, "Env"))
        {
            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;
            env.Add(new(line[..equals], line[(equals + 1)..]));
        }

        var labels = new List<KeyValuePair<string, string>>();
        if (config.ValueKind == JsonValueKind.Object && config.TryGetProperty("Labels", out var map) &&
            map.ValueKind == JsonValueKind.Object)
        {
            foreach (var label in map.EnumerateObject())
                labels.Add(new(label.Name, label.Value.ValueKind == JsonValueKind.String ? label.Value.GetString() ?? "" : ""));
        }

        return new SecretInput
        {
            Container = (root.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() ?? ""
                : "").TrimStart('/'),
            Image = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("Image", out var image) &&
                    image.ValueKind == JsonValueKind.String
                ? image.GetString() ?? ""
                : "",
            Env = env,
            Arguments = [.. Strings(config, "Entrypoint"), .. Strings(config, "Cmd")],
            Labels = labels,
        };
    }

    private static IReadOnlyList<string> Strings(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString() ?? "")]
            : [];

    // ---- finding ---------------------------------------------------------------------

    /// <summary>
    /// Every finding in every container, with <see cref="SecretFinding.SameValueAs"/> filled
    /// in. The values are compared here, in a local table that goes out of scope with this
    /// call, so the "same as" needs no hash of the value to outlive it.
    /// </summary>
    public static IReadOnlyList<SecretFinding> FindAll(IEnumerable<SecretInput> inputs)
    {
        var found = new List<(SecretFinding Finding, string Value)>();
        foreach (var input in inputs)
            found.AddRange(FindWithValues(input));

        var byValue = found.GroupBy(f => f.Value, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Finding).ToList(), StringComparer.Ordinal);

        return
        [
            .. found.Select(f => byValue.TryGetValue(f.Value, out var same)
                ? f.Finding with
                {
                    SameValueAs = [.. same.Where(o => !ReferenceEquals(o, f.Finding)).Select(o => $"{o.Container} {o.Name}").Distinct()],
                }
                : f.Finding),
        ];
    }

    /// <summary>The findings in one container.</summary>
    public static IReadOnlyList<SecretFinding> Find(SecretInput input) => FindAll([input]);

    private static List<(SecretFinding, string)> FindWithValues(SecretInput input)
    {
        var list = new List<(SecretFinding, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string where, string name, string value, Verdict verdict)
        {
            if (!seen.Add(where + "\0" + name))
                return;
            list.Add((new SecretFinding(input.Container, where, name, verdict.Kind, verdict.Severity, value.Trim().Length)
            {
                Weak = verdict.Weak,
                Image = input.Image,
            }, value.Trim()));
        }

        foreach (var (name, value) in input.Env)
        {
            if (Classify(name, value) is { } verdict)
                Add(SecretPlaces.Env, name, value, verdict);
        }

        foreach (var (name, value) in input.Labels)
        {
            if (IgnoredLabel(name))
                continue;
            if (Classify(name, value, label: true) is { } verdict)
                Add(SecretPlaces.Label, name, value, verdict);
        }

        foreach (var (name, value, verdict) in ScanArguments(input.Arguments))
            Add(SecretPlaces.Command, name, value, verdict);

        return list;
    }

    /// <summary>What a value is judged to be.</summary>
    public sealed record Verdict(string Kind, SecretSeverity Severity, bool Weak = false);

    /// <summary>
    /// Whether one name and value are a plain-text secret, and what kind — or null. Public so
    /// the table of true and false positives is tested a line at a time.
    /// </summary>
    public static Verdict? Classify(string name, string value, bool label = false)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return null;
        if (IsFileReference(name, trimmed))
            return null;

        // 1. The value's shape, under any name.
        if (Shape(trimmed) is { } shaped)
            return shaped;

        var upper = name.ToUpperInvariant();
        if (NotSecrets.Contains(upper))
            return null;

        // 2. A name that means one thing.
        if (Known.TryGetValue(upper, out var known))
            return IsPlaceholder(trimmed) ? known with { Weak = true, Severity = Min(known.Severity, SecretSeverity.Medium) } : known;

        // 3. A secret word in the name.
        var segments = Segments(name);
        if (segments.Count == 0)
            return null;
        if (Describes(segments) || segments.Any(PublicWords.Contains))
            return null;

        var word = SecretWordIn(segments);
        if (word is null)
            return null;
        var (kind, severity, passwordLike) = word.Value;

        if (HtpasswdHash().IsMatch(trimmed))
            return new Verdict("password hash", SecretSeverity.Low);

        if (IsPlaceholder(trimmed))
            return new Verdict(passwordLike ? "placeholder password" : "placeholder value",
                passwordLike ? SecretSeverity.Medium : SecretSeverity.Low, Weak: true);

        if (LooksLikeSetting(trimmed, passwordLike, label))
            return null;

        // A webhook is the secret itself; any other URL without a password in it is an address.
        if (kind != "webhook URL" && PlainUrl().IsMatch(trimmed))
            return null;

        if (!passwordLike && IsRandom(trimmed))
            return new Verdict(kind is "key" or "auth or session value" ? "random-looking key or secret" : kind, SecretSeverity.High);

        return new Verdict(kind, severity);
    }

    private static SecretSeverity Min(SecretSeverity a, SecretSeverity b) => a < b ? a : b;

    /// <summary>Tokens and credentials that say what they are by their shape.</summary>
    private static Verdict? Shape(string value)
    {
        if (PrivateKeyBlock().IsMatch(value))
            return new Verdict("private key", SecretSeverity.High);
        if (GitHubToken().IsMatch(value))
            return new Verdict("GitHub token", SecretSeverity.High);
        if (SlackWebhook().IsMatch(value))
            return new Verdict("Slack webhook", SecretSeverity.High);
        if (SlackToken().IsMatch(value))
            return new Verdict("Slack token", SecretSeverity.High);
        if (AwsKey().IsMatch(value))
            return new Verdict("AWS access key", SecretSeverity.High);
        if (DiscordWebhook().IsMatch(value))
            return new Verdict("Discord webhook", SecretSeverity.High);
        if (TelegramToken().IsMatch(value))
            return new Verdict("Telegram bot token", SecretSeverity.High);
        if (TailscaleKey().IsMatch(value))
            return new Verdict("Tailscale auth key", SecretSeverity.High);
        if (Jwt().IsMatch(value))
            return new Verdict("JWT", SecretSeverity.High);
        if (CredentialUrl().IsMatch(value) && !UrlPasswordIsReference().IsMatch(value))
            return new Verdict("URL with a password", SecretSeverity.Medium);
        if (ConnectionStringPassword().IsMatch(value))
            return new Verdict("connection string with a password", SecretSeverity.Medium);
        if (PlainUrl().IsMatch(value) && TokenInQuery().IsMatch(value))
            return new Verdict("URL with a token in it", SecretSeverity.Medium);
        return null;
    }

    /// <summary>
    /// A value that says where the secret is rather than what it is: Docker's and most
    /// official images' <c>*_FILE</c> convention, linuxserver's <c>FILE__VAR</c>, and anything
    /// pointing into <c>/run/secrets</c>. This is the fix, so it must never be a finding.
    /// </summary>
    public static bool IsFileReference(string name, string value) =>
        name.EndsWith("_FILE", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("__FILE", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("FILE__", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("/run/secrets/", StringComparison.Ordinal) ||
        value.StartsWith("/var/run/secrets/", StringComparison.Ordinal);

    /// <summary>Settings every home-lab container has and none of them is a secret.</summary>
    private static readonly HashSet<string> NotSecrets = new(StringComparer.Ordinal)
    {
        "PUID", "PGID", "UID", "GID", "USER_ID", "GROUP_ID", "TZ", "UMASK", "UMASK_SET", "PATH", "HOME", "LANG",
        "LANGUAGE", "LC_ALL", "HOSTNAME", "TERM", "SHELL", "PWD", "OLDPWD", "VERSION", "DOCKER_MODS", "S6_VERBOSITY",
        "NVIDIA_VISIBLE_DEVICES", "NVIDIA_DRIVER_CAPABILITIES", "WEBUI_PORT", "PORT", "LOG_LEVEL", "DEBUG",
        "KEYBOARD", "KEYMAP", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME",
    };

    /// <summary>
    /// Names owned by one product, whose meaning is known exactly — mostly the VPN, tunnel
    /// and DNS containers, which are the ones whose credentials work from anywhere.
    /// </summary>
    private static readonly Dictionary<string, Verdict> Known = new(StringComparer.Ordinal)
    {
        // gluetun, transmission-openvpn, qbittorrentvpn and friends.
        ["OPENVPN_USER"] = new("VPN username", SecretSeverity.Medium),
        ["OPENVPN_USERNAME"] = new("VPN username", SecretSeverity.Medium),
        ["VPN_USER"] = new("VPN username", SecretSeverity.Medium),
        ["VPN_USERNAME"] = new("VPN username", SecretSeverity.Medium),
        ["PIA_USER"] = new("VPN username", SecretSeverity.Medium),
        ["OPENVPN_PASSWORD"] = new("VPN password", SecretSeverity.High),
        ["OPENVPN_PASS"] = new("VPN password", SecretSeverity.High),
        ["VPN_PASSWORD"] = new("VPN password", SecretSeverity.High),
        ["VPN_PASS"] = new("VPN password", SecretSeverity.High),
        ["PIA_PASS"] = new("VPN password", SecretSeverity.High),
        ["WIREGUARD_PRIVATE_KEY"] = new("WireGuard private key", SecretSeverity.High),
        ["WG_PRIVATE_KEY"] = new("WireGuard private key", SecretSeverity.High),
        ["WIREGUARD_PRESHARED_KEY"] = new("WireGuard pre-shared key", SecretSeverity.High),
        // Expires four minutes after it was made, so by the time anybody reads this it is spent.
        ["PLEX_CLAIM"] = new("Plex claim token", SecretSeverity.Low),
        ["CF_API_TOKEN"] = new("Cloudflare API token", SecretSeverity.High),
        ["CF_DNS_API_TOKEN"] = new("Cloudflare API token", SecretSeverity.High),
        ["CF_ZONE_API_TOKEN"] = new("Cloudflare API token", SecretSeverity.High),
        ["CLOUDFLARE_API_TOKEN"] = new("Cloudflare API token", SecretSeverity.High),
        ["CLOUDFLARE_DNS_API_TOKEN"] = new("Cloudflare API token", SecretSeverity.High),
        ["CF_API_KEY"] = new("Cloudflare global API key", SecretSeverity.High),
        ["CLOUDFLARE_API_KEY"] = new("Cloudflare global API key", SecretSeverity.High),
        ["TUNNEL_TOKEN"] = new("Cloudflare tunnel token", SecretSeverity.High),
        ["TS_AUTHKEY"] = new("Tailscale auth key", SecretSeverity.High),
        ["TS_AUTH_KEY"] = new("Tailscale auth key", SecretSeverity.High),
        ["TAILSCALE_AUTHKEY"] = new("Tailscale auth key", SecretSeverity.High),
        ["WATCHTOWER_NOTIFICATION_URL"] = new("notification URL with a token", SecretSeverity.Medium),
    };

    /// <summary>
    /// A last word that makes the name about a secret rather than the secret:
    /// <c>PASSWORD_FILE</c>, <c>TOKEN_TTL</c>, <c>AUTH_METHOD</c>, <c>SESSION_TIMEOUT</c>.
    /// </summary>
    private static readonly HashSet<string> Describing = new(StringComparer.Ordinal)
    {
        "FILE", "PATH", "DIR", "LOCATION", "HOST", "HOSTNAME", "PORT", "USER", "USERNAME", "EMAIL", "ENABLED",
        "ENABLE", "DISABLED", "DISABLE", "REQUIRED", "METHOD", "METHODS", "TYPE", "MODE", "PROVIDER", "TIMEOUT",
        "TTL", "EXPIRY", "EXPIRE", "EXPIRES", "EXPIRATION", "LIFETIME", "DURATION", "AGE", "LENGTH", "SIZE",
        "BITS", "ALGORITHM", "ALG", "NAME", "ID", "HEADER", "HEADERS", "FIELD", "PARAM", "ROTATION", "INTERVAL",
        "DOMAIN", "ISSUER", "AUDIENCE", "SCOPE", "SCOPES", "STRATEGY", "BACKEND", "STORE", "FORMAT", "POLICY",
        "MIN", "MAX", "COUNT", "RETRIES", "ATTEMPTS", "SECURE", "SAMESITE", "HTTPONLY", "PREFIX", "SUFFIX",
        "USERSFILE", "ADDRESS",
    };

    /// <summary>Words that make a key public — a fingerprint to verify against, not a secret to keep.</summary>
    private static readonly HashSet<string> PublicWords = new(StringComparer.Ordinal)
    {
        "PUBLIC", "PUB", "PUBKEY", "GPG", "PGP", "SHA", "SHA1", "SHA256", "SHA512", "MD5", "CHECKSUM", "FINGERPRINT",
        "KEYID", "KEYSERVER", "KEYRING", "KEYBOARD", "KEYMAP", "KEYS",
    };

    private static bool Describes(IReadOnlyList<string> segments)
    {
        var last = segments[^1];
        return Describing.Contains(last) || (last.Length > 4 && last.EndsWith("FILE", StringComparison.Ordinal));
    }

    /// <summary>
    /// The secret word in a name, if any, and what it makes the value: its kind, how much it
    /// matters before looking at the value, and whether it is a password (which counts
    /// whatever it holds, where an <c>AUTH</c> holding "basic" is a setting).
    ///
    /// Short words count only as a whole segment, so <c>DB_PASS</c> is a password and
    /// <c>PASSTHROUGH</c> is not; long distinctive ones count anywhere, so
    /// <c>ADMINPASSWORD</c> is one too.
    /// </summary>
    private static (string Kind, SecretSeverity Severity, bool PasswordLike)? SecretWordIn(IReadOnlyList<string> segments)
    {
        bool Has(params string[] words) => segments.Any(s => words.Contains(s));
        bool Contains(params string[] words) => segments.Any(s => words.Any(w => s.Contains(w, StringComparison.Ordinal)));

        if (Has("PASS", "PWD", "PW") || Contains("PASSWORD", "PASSWD", "PASSPHRASE"))
            return ("password", SecretSeverity.Medium, true);
        if (Contains("WEBHOOK"))
            return ("webhook URL", SecretSeverity.Medium, false);
        if (Contains("CREDENTIAL") || Has("CREDS"))
            return ("credentials", SecretSeverity.Medium, false);
        if (Has("PRIVATE") || Contains("PRIVKEY"))
            return ("private key", SecretSeverity.Medium, false);
        if (Contains("APIKEY") || HasPair(segments, "API", "KEY"))
            return ("API key", SecretSeverity.Medium, false);
        if (Contains("SECRET"))
            return ("secret", SecretSeverity.Medium, false);
        if (Contains("TOKEN", "AUTHKEY"))
            return ("token", SecretSeverity.Medium, false);
        if (Has("DSN"))
            return ("DSN", SecretSeverity.Low, false);
        if (Contains("CONNECTIONSTRING", "CONNSTR") || HasPair(segments, "CONNECTION", "STRING") || HasPair(segments, "CONN", "STR"))
            return ("connection string", SecretSeverity.Medium, false);
        if (Has("KEY"))
            return ("key", SecretSeverity.Low, false);
        if (Has("AUTH", "SESSION", "COOKIE", "SALT"))
            return ("auth or session value", SecretSeverity.Low, false);
        return null;
    }

    private static bool HasPair(IReadOnlyList<string> segments, string first, string second)
    {
        for (var i = 0; i + 1 < segments.Count; i++)
        {
            if (segments[i] == first && segments[i + 1] == second)
                return true;
        }
        return false;
    }

    /// <summary>A name in upper-case words: <c>dbPassword</c>, <c>DB-PASSWORD</c> and <c>db.password</c> are all DB, PASSWORD.</summary>
    public static IReadOnlyList<string> Segments(string name)
    {
        var spaced = CamelBoundary().Replace(name.TrimStart('-'), "$1_$2");
        return [.. SegmentSplit().Split(spaced).Where(s => s.Length > 0).Select(s => s.ToUpperInvariant())];
    }

    /// <summary>
    /// A value that is plainly a setting rather than a secret, for a name that only might
    /// be one: <c>true</c>, a number, a path, a short word under <c>AUTH</c>. A password
    /// name counts whatever it holds — <c>123456</c> is a password, and a bad one.
    /// </summary>
    private static bool LooksLikeSetting(string value, bool passwordLike, bool label)
    {
        if (passwordLike)
            return value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}');
        if (Settingish.Contains(value.ToLowerInvariant()))
            return true;
        if (value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}'))
            return true;
        if (value.Length <= 6 && value.All(char.IsAsciiDigit))
            return true;
        if (PathValue().IsMatch(value))
            return true;
        // A word or a list of words, like "basic" or "Remote-User,Remote-Groups": no digits,
        // nothing random about it. Under a password name this would still count.
        if (value.Length <= 40 && WordsOnly().IsMatch(value))
            return true;
        // Labels are mostly routing configuration — a Traefik rule mentions "auth" and is
        // still a rule — so a label needs to look random before it counts.
        return label && !IsRandom(value);
    }

    private static readonly HashSet<string> Settingish = new(StringComparer.Ordinal)
    {
        "true", "false", "yes", "no", "on", "off", "enabled", "disabled", "none", "null", "nil", "required", "optional",
    };

    /// <summary>
    /// The values people leave in from the example compose file. Still a finding — a weak
    /// password is worse than a strong one, not better — but marked so the advice says to
    /// change it rather than to hide it.
    /// </summary>
    public static bool IsPlaceholder(string value)
    {
        var lower = value.Trim().ToLowerInvariant();
        return Placeholders.Contains(lower) || PlaceholderShape().IsMatch(lower);
    }

    private static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal)
    {
        "changeme", "change_me", "change-me", "changeit", "change_this", "changethis", "password", "passw0rd",
        "p@ssw0rd", "password1", "password123", "secret", "admin", "administrator", "root", "example", "default",
        "test", "testing", "letmein", "qwerty", "123456", "12345678", "1234", "pass", "token", "replaceme",
        "replace_me", "replace-me", "placeholder", "supersecret", "super_secret", "mysecret", "secretkey",
        "secret_key", "insecure", "todo", "fixme",
    };

    /// <summary>
    /// Random enough to be a generated key rather than something a person typed: long, and
    /// using its alphabet evenly. 3.3 bits a character passes a 32-digit hex key and a
    /// base64 token and fails "correcthorsebatterystaple"-style words and repeated text.
    /// </summary>
    public static bool IsRandom(string value) => value.Length >= 20 && TokenAlphabet().IsMatch(value) && Entropy(value) >= 3.3;

    /// <summary>Shannon entropy in bits per character.</summary>
    public static double Entropy(string value)
    {
        if (value.Length == 0)
            return 0;
        var counts = new Dictionary<char, int>();
        foreach (var ch in value)
            counts[ch] = counts.GetValueOrDefault(ch) + 1;
        double entropy = 0;
        foreach (var count in counts.Values)
        {
            var p = (double)count / value.Length;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    /// <summary>Labels Docker, Compose and image builders write, none of which is anybody's secret.</summary>
    private static bool IgnoredLabel(string name) =>
        name.StartsWith("com.docker.", StringComparison.Ordinal) ||
        name.StartsWith("org.opencontainers.", StringComparison.Ordinal) ||
        name.StartsWith("org.label-schema.", StringComparison.Ordinal) ||
        name.StartsWith("io.buildah.", StringComparison.Ordinal) ||
        name == "maintainer";

    /// <summary>
    /// Secrets on the command line: <c>--password=x</c>, <c>--token x</c>, <c>KEY=x</c>, and
    /// anything shaped like a token wherever it is. A shell command (<c>sh -c "…"</c>) is
    /// split into words and read the same way.
    /// </summary>
    private static IEnumerable<(string Name, string Value, Verdict Verdict)> ScanArguments(IReadOnlyList<string> arguments)
    {
        var words = new List<string>();
        foreach (var argument in arguments)
        {
            if (argument.Contains(' ', StringComparison.Ordinal) && !argument.StartsWith("-----BEGIN", StringComparison.Ordinal))
                words.AddRange(argument.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('"', '\'')));
            else
                words.Add(argument);
        }

        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var equals = word.IndexOf('=');
            var url = word.IndexOf("://", StringComparison.Ordinal);
            if (equals > 0 && (url < 0 || url > equals))
            {
                var name = word[..equals];
                var value = word[(equals + 1)..];
                if (Classify(name.TrimStart('-'), value) is { } verdict)
                {
                    yield return (name, value, verdict);
                    continue;
                }
            }
            else if (word.StartsWith('-') && word.Length > 1 && i + 1 < words.Count && !words[i + 1].StartsWith('-'))
            {
                // "--token abc": the flag names it, the next word is it. Only a flag with a
                // secret word in it, or every "--port 80" would be read as a key/value.
                var flag = word.TrimStart('-');
                if (SecretWordIn(Segments(flag)) is not null || Known.ContainsKey(flag.ToUpperInvariant()))
                {
                    if (Classify(flag, words[i + 1]) is { } verdict)
                    {
                        yield return (word, words[i + 1], verdict);
                        i++;
                        continue;
                    }
                }
            }

            if (Shape(word) is { } shaped)
                yield return ($"argument {i + 1}", word, shaped);
        }
    }

    // ---- ignoring ----------------------------------------------------------------------

    /// <summary>What the ignore list stores for a finding.</summary>
    public static string IgnoreKey(string container, string name) => $"{container}|{name}";

    /// <summary>The ignore list as stored — a JSON array of keys — read forgivingly.</summary>
    public static IReadOnlySet<string> ParseIgnored(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            return new HashSet<string>(JsonSerializer.Deserialize<List<string>>(stored) ?? [], StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public static string FormatIgnored(IEnumerable<string> keys) =>
        JsonSerializer.Serialize(keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());

    // ---- advice ------------------------------------------------------------------------

    /// <summary>Whether an image is one of linuxserver.io's, which read <c>FILE__VAR</c> for any variable.</summary>
    public static bool IsLinuxServer(string image) =>
        image.Contains("linuxserver/", StringComparison.OrdinalIgnoreCase) ||
        image.StartsWith("lscr.io/", StringComparison.OrdinalIgnoreCase);

    /// <summary>What to do about one finding: where to move it, and whether to rotate it.</summary>
    public static IReadOnlyList<string> Advise(SecretFinding finding)
    {
        var advice = new List<string>();
        var lower = finding.Name.ToLowerInvariant().Trim('-');

        if (finding.Kind == "Plex claim token")
        {
            advice.Add("A claim token only works for four minutes. Once the server shows up in your Plex account, delete the variable.");
            return advice;
        }

        if (finding.Weak)
            advice.Add("It looks like a placeholder or a default. Change it to a long random one — whatever it guards, the first guess anybody tries is this.");

        switch (finding.Where)
        {
            case SecretPlaces.Env:
                advice.Add($"Move it out of the compose file: put {finding.Name}=… in an .env file next to docker-compose.yml, " +
                           $"write {finding.Name}: ${{{finding.Name}}} in the compose file, keep .env out of git and run chmod 600 .env.");
                advice.Add(IsLinuxServer(finding.Image)
                    ? $"This is a linuxserver image, so it can read the value from a file instead: mount a Docker secret and set FILE__{finding.Name}=/run/secrets/{lower}. Then it is not in the container's settings at all."
                    : $"Better still, if the image supports a {finding.Name}_FILE variable (most official database images do), mount a Docker secret and set {finding.Name}_FILE=/run/secrets/{lower}.");
                break;
            case SecretPlaces.Command:
                advice.Add("Anything that can list processes on the host can read a command line. Pass it through an environment variable from an .env file, or a file the program reads, instead.");
                break;
            case SecretPlaces.Label:
                advice.Add(finding.Kind == "password hash"
                    ? "It is a hash, not the password, but a weak password can be cracked from it. Traefik can read the same thing from a file: use usersFile instead of users."
                    : "Labels can be read by anything that lists containers, even through a socket proxy. Move it to a file the program reads.");
                break;
        }

        if (finding.Severity == SecretSeverity.High)
            advice.Add("If this compose file was ever shared, pasted or committed, treat it as leaked: make a new one and revoke this one.");

        return advice;
    }

    // ---- patterns ----------------------------------------------------------------------

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bxox[abposr]-[A-Za-z0-9-]{10,}", RegexOptions.CultureInvariant)]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"https://hooks\.slack\.com/services/T[A-Za-z0-9]+/B[A-Za-z0-9]+/[A-Za-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex SlackWebhook();

    [GeneratedRegex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AwsKey();

    [GeneratedRegex(@"https://(ptb\.|canary\.)?discord(app)?\.com/api/webhooks/\d+/[A-Za-z0-9_-]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DiscordWebhook();

    [GeneratedRegex(@"(?<![\d])\d{8,10}:AA[A-Za-z0-9_-]{33}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant)]
    private static partial Regex TelegramToken();

    [GeneratedRegex(@"\btskey-[a-z]+-[A-Za-z0-9-]{10,}", RegexOptions.CultureInvariant)]
    private static partial Regex TailscaleKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(ContainerConfigs.CredentialUrlPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialUrl();

    /// <summary>A URL whose "password" is an unexpanded <c>${VAR}</c> — a reference, not a secret.</summary>
    [GeneratedRegex(@"://[^/\s:@]*:\$\{[^}]+\}@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPasswordIsReference();

    [GeneratedRegex(@"(^|;)\s*(password|pwd)\s*=\s*[^;\s$][^;]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringPassword();

    [GeneratedRegex(@"[:\s]\$(apr1|2[abxy]|5|6|argon2id?)\$|\{SHA\}", RegexOptions.CultureInvariant)]
    private static partial Regex HtpasswdHash();

    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*://\S+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlainUrl();

    [GeneratedRegex(@"[?&](token|key|apikey|api_key|access_token|secret|password|auth)=[^&\s]{16,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenInQuery();

    [GeneratedRegex(@"^(\.{0,2}/|~/)[^\s]*(/|\.[a-z0-9]{1,5})[^\s]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathValue();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z ,._:-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex WordsOnly();

    [GeneratedRegex(@"^(<.*>|\[.*\]|x{3,}|\*{3,}|(your|enter|insert|put)[_-][a-z_-]*|your(password|pass|secret|token|key|apikey)|my[_-]?(password|pass|secret|token|key|apikey|api_key)|.*_here)$", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderShape();

    /// <summary>The characters keys and tokens are written in: no spaces, quotes, braces or colons, so JSON and URLs are not "random".</summary>
    [GeneratedRegex(@"^[A-Za-z0-9+/=_\-.~]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenAlphabet();

    [GeneratedRegex(@"([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex CamelBoundary();

    [GeneratedRegex(@"[_\-.\s/:]+", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentSplit();
}
