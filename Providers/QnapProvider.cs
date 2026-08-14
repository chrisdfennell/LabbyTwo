using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// QNAP QTS. Handles the authLogin.cgi session per connection, so more than one NAS can
/// be added. QTS XML shifts between firmware versions, so every field is read by
/// descendant name and tolerated missing.
/// </summary>
public sealed class QnapProvider(IHttpClientFactory httpFactory, ILogger<QnapProvider> log) : IConnectionProvider
{
    public string Type => "qnap";
    public string DisplayName => "QNAP NAS";
    public string Icon => "💾";
    public string Category => "Storage";
    public string Description => "QTS system stats, temperatures, and volume usage. Create a dedicated account — accounts with 2FA cannot use this API.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("host", "Host", FieldKind.Text, "192.168.1.50", Required: true),
        new("port", "Port", FieldKind.Number, Default: "8080", Help: "QTS web port — 8080 for http, 443 for https by default."),
        new("https", "Use HTTPS", FieldKind.Bool, Default: "false"),
        new("username", "Username", FieldKind.Text, Required: true),
        new("password", "Password", FieldKind.Password, Required: true),
        new("mac", "MAC address", FieldKind.Text, "00:11:22:33:44:55",
            Help: "Only needed for Wake on LAN, which is the one thing that has to work while the NAS is off. " +
                  "Enable it on the NAS first: Control Panel → System → Power → Wake on LAN. " +
                  "The magic packet is broadcast from wherever LabbyTwo runs, so a container on a bridge " +
                  "network will not reach your LAN — that one needs host or macvlan networking.")
        {
            Advanced = true,
        },
    ];

    private readonly ConcurrentDictionary<string, string> _sessions = new();

    /// <summary>
    /// What the last probe saw. The NAS card used to fetch its own copy on every render,
    /// which meant three round trips to QTS for one card — the probe's, plus two more —
    /// and QTS is slow enough under load that those extra ones regularly timed out and
    /// showed as "Operation canceled" on an otherwise healthy NAS. The probe already has
    /// this data, so the card reads it instead.
    /// </summary>
    private readonly ConcurrentDictionary<string, Snapshot> _latest = new();

    /// <summary>
    /// When each connection was last asked about firmware. Separate from the snapshot
    /// because the answer outlives it: the snapshot is replaced every sweep, and asking
    /// QTS about firmware every thirty seconds is 2,880 requests a day for news that
    /// arrives a few times a year.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _firmwareCheckedAt = new();

    private static readonly TimeSpan FirmwareCheckInterval = TimeSpan.FromHours(6);

    /// <summary>Everything one probe learned, kept so the cards can redraw without asking QTS again.</summary>
    public sealed record Snapshot(
        SystemInfo Info,
        IReadOnlyList<VolumeInfo> Volumes,
        IReadOnlyList<DiskInfo> Disks,
        string? AvailableFirmware,
        DateTimeOffset At);

    /// <summary>The most recent reading, or null if this connection has not been probed yet.</summary>
    public Snapshot? LatestSnapshot(Connection connection) =>
        _latest.TryGetValue(connection.Id, out var cached) ? cached : null;

    /// <summary>
    /// The system and volume half of <see cref="LatestSnapshot"/>. Kept in this shape
    /// because a plugin compiled against it would stop working if the shape changed, and
    /// this codebase has already paid once to learn that — see <c>FieldSpec.ProviderFilter</c>.
    /// New readings go on the snapshot instead.
    /// </summary>
    public (SystemInfo Info, IReadOnlyList<VolumeInfo> Volumes)? Latest(Connection connection) =>
        LatestSnapshot(connection) is { } snapshot ? (snapshot.Info, snapshot.Volumes) : null;

    public sealed record SystemInfo(
        string? Model, string? Firmware, string? HostName, TimeSpan? Uptime,
        double? CpuPercent, double? TotalMemoryMb, double? FreeMemoryMb,
        double? CpuTempC, double? SystemTempC)
    {
        /// <summary>
        /// Fans, by the name QTS gave them. Init-only rather than a tenth constructor
        /// parameter, for the reason spelled out on <see cref="Snapshot"/>.
        /// </summary>
        public IReadOnlyList<FanInfo> Fans { get; init; } = [];

        public string? Serial { get; init; }
    }

    public sealed record FanInfo(string Label, double Rpm);

    public sealed record VolumeInfo(string Label, long TotalBytes, long FreeBytes)
    {
        public long UsedBytes => TotalBytes - FreeBytes;
        public double UsedPercent => TotalBytes > 0 ? UsedBytes * 100d / TotalBytes : 0;
    }

    /// <summary>
    /// One physical drive as SMART sees it. <see cref="Health"/> is QTS's own word for it
    /// ("Good", "Warning", "Abnormal"), passed through rather than mapped, because the
    /// vocabulary shifts between firmware versions and inventing a new one on top only
    /// adds a second thing that can be wrong.
    /// </summary>
    public sealed record DiskInfo(string Slot, string? Model, string? Health, double? TempC, long CapacityBytes)
    {
        /// <summary>
        /// Anything QTS is not calling fine. Matched on the good words rather than the bad
        /// ones: a firmware that invents a new failure word should read as a failure, and a
        /// firmware that invents a new healthy word costs one spurious warning.
        /// </summary>
        public bool IsFailing => Health is { Length: > 0 } word
            && word is not ("Good" or "Normal" or "GOOD" or "NORMAL" or "--" or "Ready");
    }

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("cpu_percent", "CPU", "%", 1),
        new("ram_percent", "Memory", "%", 1),
        new("temp_c", "CPU temperature", "°C", 1),
        new("disk_percent", "Fullest volume", "%", 1),
        new("uptime_days", "Uptime", " days", 1),
        new("disks_failing", "Disks not healthy"),
        new("disk_temp_max", "Hottest disk", "°C", 1),
        new("fan_rpm_min", "Slowest fan", " rpm"),
        new("firmware_update", "Firmware update waiting"),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Volume nearly full", "disk_percent", Comparison.Above, 90, ClearThreshold: 85, ForMinutes: 10,
            Why: "The one that actually loses data if ignored."),
        new("Running hot", "temp_c", Comparison.Above, 60, ClearThreshold: 55, ForMinutes: 15,
            Why: "Usually a failed fan or a blocked vent."),
        new("A disk is failing SMART", "disks_failing", Comparison.Above, 0, ForMinutes: 5,
            Why: "The NAS knows before the array does. This is the warning that arrives while the rebuild is still cheap."),
        new("A fan has stopped", "fan_rpm_min", Comparison.Below, 200, ForMinutes: 10,
            Why: "A stopped fan reads as zero rpm and shows up nowhere else until the temperature alert fires an hour later."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var info = await SystemInfoAsync(connection, ct);
            stopwatch.Stop();

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            if (info.CpuPercent is { } cpu)
                metrics["cpu_percent"] = cpu;
            if (info is { TotalMemoryMb: > 0 } and { FreeMemoryMb: not null })
                metrics["ram_percent"] = (info.TotalMemoryMb.Value - info.FreeMemoryMb.Value) / info.TotalMemoryMb.Value * 100;
            if (info.CpuTempC is { } cpuTemp)
                metrics["temp_c"] = cpuTemp;
            if (info.Uptime is { } uptime)
                metrics["uptime_days"] = uptime.TotalDays;

            // Volume fullness is the number people actually want alerts on, so fold the
            // busiest volume into the same probe rather than making it a second poll.
            IReadOnlyList<VolumeInfo> volumes = [];
            try
            {
                volumes = await VolumesAsync(connection, ct);
                if (volumes.Count > 0)
                    metrics["disk_percent"] = volumes.Max(v => v.UsedPercent);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Volume usage unavailable for {Connection}", connection.Name);
            }

            if (info.Fans.Count > 0)
                metrics["fan_rpm_min"] = info.Fans.Min(f => f.Rpm);

            // SMART is the reading that matters most and the one QTS is slowest to give
            // up, so like volumes it is folded in here and allowed to be missing rather
            // than being made a second poll that can fail on its own.
            IReadOnlyList<DiskInfo> disks = [];
            try
            {
                disks = await DisksAsync(connection, ct);
                if (disks.Count > 0)
                {
                    metrics["disks_failing"] = disks.Count(d => d.IsFailing);

                    // A drive that reports no temperature — an SSD on some firmware — must
                    // not drag the maximum down to zero and read as an unusually cool array.
                    var temperatures = disks.Select(d => d.TempC).OfType<double>().ToList();
                    if (temperatures.Count > 0)
                        metrics["disk_temp_max"] = temperatures.Max();
                }
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "SMART data unavailable for {Connection}", connection.Name);
            }

            // Asked rarely and remembered in between. Firmware changes a few times a year,
            // and this provider has already been through one round of costing a busy NAS
            // more round trips per sweep than it could answer — the whole reason the cards
            // read from the probe instead of fetching their own copy.
            var firmwareDue = !_firmwareCheckedAt.TryGetValue(connection.Id, out var checkedAt)
                              || DateTimeOffset.Now - checkedAt > FirmwareCheckInterval;

            var availableFirmware = LatestSnapshot(connection)?.AvailableFirmware;
            if (firmwareDue)
            {
                try
                {
                    availableFirmware = await AvailableFirmwareAsync(connection, info.Firmware, ct);
                    _firmwareCheckedAt[connection.Id] = DateTimeOffset.Now;
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "Firmware check unavailable for {Connection}", connection.Name);
                }
            }
            metrics["firmware_update"] = availableFirmware is null ? 0 : 1;

            // Kept for the NAS card, so it draws from this rather than asking QTS again.
            _latest[connection.Id] = new Snapshot(info, volumes, disks, availableFirmware, DateTimeOffset.Now);

            var details = new Dictionary<string, string>();
            if (info.Model is { Length: > 0 } model)
                details["Model"] = model;
            if (info.Firmware is { Length: > 0 } firmware)
                details["Firmware"] = firmware;
            if (availableFirmware is { Length: > 0 } waiting)
                details["Update waiting"] = waiting;

            var unhealthy = disks.Count(d => d.IsFailing);
            if (unhealthy > 0)
                details["Disks not healthy"] = unhealthy.ToString(CultureInfo.InvariantCulture);

            return ProbeResult.Up(stopwatch.Elapsed, info.Model is { Length: > 0 } m ? m : "Connected", metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            InvalidateSession(connection);
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("host")));
        }
    }

    public async Task<SystemInfo> SystemInfoAsync(Connection connection, CancellationToken ct)
    {
        var doc = await GetXmlAsync(connection, sid => $"cgi-bin/management/manaRequest.cgi?subfunc=sysinfo&sid={sid}", ct);

        TimeSpan? uptime = null;
        var days = Num(doc, "uptime_day");
        var hours = Num(doc, "uptime_hour");
        var minutes = Num(doc, "uptime_min");
        if (days is not null || hours is not null || minutes is not null)
            uptime = new TimeSpan((int)(days ?? 0), (int)(hours ?? 0), (int)(minutes ?? 0), 0);

        return new SystemInfo(
            Str(doc, "displayModelName") ?? Str(doc, "modelName"),
            Str(doc, "version"),
            Str(doc, "hostname") ?? Str(doc, "server_name"),
            uptime,
            Num(doc, "cpu_usage"),
            Num(doc, "total_memory"),
            Num(doc, "free_memory"),
            Num(doc, "cpu_tempc"),
            Num(doc, "sys_tempc"))
        {
            Fans = Fans(doc),
            Serial = Str(doc, "serial_number") ?? Str(doc, "serialNumber"),
        };
    }

    /// <summary>
    /// Fans out of the same sysinfo document, so watching them costs no extra round trip.
    /// Found by element name rather than by a fixed list, because the count and the naming
    /// both depend on the chassis — a two-bay has one <c>sysfan1</c>, a rackmount has five
    /// and a CPU fan besides.
    /// </summary>
    private static IReadOnlyList<FanInfo> Fans(XContainer doc)
    {
        var fans = new List<FanInfo>();
        foreach (var element in doc.Descendants())
        {
            var name = element.Name.LocalName;
            if (!name.Contains("fan", StringComparison.OrdinalIgnoreCase) || element.HasElements)
                continue;

            // "Fan status" and similar carry words rather than a speed; a fan reporting no
            // number is a fan we know nothing about, which is not the same as a stopped one.
            if (Number(element.Value) is not { } rpm)
                continue;

            fans.Add(new FanInfo(name, rpm));
        }
        return fans;
    }

    public async Task<IReadOnlyList<VolumeInfo>> VolumesAsync(Connection connection, CancellationToken ct)
    {
        var doc = await GetXmlAsync(connection,
            sid => $"cgi-bin/management/chartReq.cgi?chart_func=disk_usage&disk_select=all&include=all&sid={sid}", ct);

        var volumes = new List<VolumeInfo>();
        foreach (var volume in doc.Descendants("volume"))
        {
            var label = Str(volume, "volumeLabel") ?? Str(volume, "volumeValue") ?? $"Volume {volumes.Count + 1}";
            var total = (long)(Num(volume, "total_size") ?? 0);
            var free = (long)(Num(volume, "free_size") ?? 0);
            if (total > 0)
                volumes.Add(new VolumeInfo(label.Trim(), total, free));
        }
        return volumes;
    }

    /// <summary>
    /// SMART, per physical drive. This is the reading a NAS dashboard exists for: volume
    /// fullness is a chore you can see coming for weeks, whereas a drive going bad is
    /// visible only here and only until the array notices for you.
    /// </summary>
    public async Task<IReadOnlyList<DiskInfo>> DisksAsync(Connection connection, CancellationToken ct)
    {
        var doc = await GetXmlAsync(connection, sid => $"cgi-bin/disk/qsmart.cgi?func=all_hd_data&sid={sid}", ct);

        var disks = new List<DiskInfo>();
        foreach (var entry in doc.Descendants("entry"))
        {
            // Temperature arrives as <Temperature><oC>38</oC><oF>100</oF></Temperature> on
            // most firmware and as a plain decorated number on some, so try both.
            var temperature = Num(entry, "oC") ?? Num(entry, "Temperature");

            var slot = Str(entry, "HDNo") ?? Str(entry, "hd_no") ?? $"Disk {disks.Count + 1}";
            disks.Add(new DiskInfo(
                slot.Trim(),
                Str(entry, "Model")?.Trim(),
                Str(entry, "Health")?.Trim(),
                temperature,
                (long)(Num(entry, "Capacity_bytes") ?? Num(entry, "capacity") ?? 0)));
        }
        return disks;
    }

    /// <summary>
    /// The firmware QNAP is offering, or null when there is nothing waiting. Deliberately
    /// conservative: QTS reports this endpoint differently across versions, and the two
    /// failure modes are not equal — a missed update notice costs a week, whereas an
    /// update notice that never clears trains you to ignore the one that matters. So an
    /// answer that cannot be told apart from the installed version counts as "nothing".
    /// </summary>
    public async Task<string?> AvailableFirmwareAsync(Connection connection, string? installed, CancellationToken ct)
    {
        var doc = await GetXmlAsync(connection, sid => $"cgi-bin/sys/sysRequest.cgi?subfunc=firm_update&sid={sid}", ct);

        // newVersion and availVersion mean what they say. A bare <version> does not: several
        // QTS versions answer this endpoint with the firmware already installed, so it is
        // only usable as an offer when there is something to compare it against.
        var stated = Str(doc, "newVersion") ?? Str(doc, "availVersion");
        if ((stated ?? Str(doc, "version")) is not { } offered)
            return null;

        var current = installed?.Trim() ?? "";
        if (stated is null && current.Length == 0)
            return null;

        // Whatever is installed already mentions the offered version — either exactly, or
        // as "5.1.0" inside "5.1.0 (20240115)" — is this endpoint reporting the current
        // firmware rather than a new one.
        if (current.Contains(offered, StringComparison.OrdinalIgnoreCase))
            return null;

        // A build number, when there is one, so "5.1.0" against an installed "5.1.0" on an
        // older build still reads as the update it is.
        return (Str(doc, "newBuild") ?? Str(doc, "build")) is { Length: > 0 } build
            ? $"{offered} build {build}"
            : offered;
    }

    // ---------- Controls ----------

    public IReadOnlyList<ProviderAction> Actions =>
    [
        new("restart", "Restart", "🔁")
        {
            Description = "Reboots QTS. Anything served off this NAS goes with it.",
            ConfirmMessage = "Everything running on the NAS stops — shares, containers, VMs and whatever is streaming from it. It normally comes back in a few minutes.",
            Dangerous = true,
            // Long enough for a spinning-rust box with a parity check on the way up. An
            // alert about a machine you personally rebooted teaches you to ignore alerts.
            Disrupts = TimeSpan.FromMinutes(15),
        },
        new("shutdown", "Shut down", "⏻")
        {
            Description = "Powers the NAS off. It will not come back on its own.",
            ConfirmMessage = "The NAS powers off and stays off. Unless Wake on LAN is set up, bringing it back means walking to it and pressing the button.",
            Dangerous = true,
            // Until tomorrow. A box that is off on purpose has nothing to say for as long
            // as it stays off, and the longest silence offered anywhere else is one night.
            Disrupts = TimeSpan.FromHours(12),
        },
        new("wake", "Wake on LAN", "⏰")
        {
            Description = "Broadcasts a magic packet. Needs the MAC address on this connection.",
            // Nothing is lost by waking a NAS that is already awake, and the moment you
            // want this button is the moment you least want another dialog.
            Confirms = false,
        },
    ];

    /// <summary>
    /// Wake on LAN only appears once there is a MAC to send it to. A button that cannot
    /// work is worse than no button: it is indistinguishable from a broken one.
    /// </summary>
    public IReadOnlyList<ProviderAction> ActionsFor(Connection connection) =>
        MacAddress(connection) is null
            ? [.. Actions.Where(action => action.Id != "wake")]
            : Actions;

    public async Task<ActionResult> RunActionAsync(
        Connection connection, ProviderAction action, SettingsBag input, CancellationToken ct)
    {
        switch (action.Id)
        {
            case "restart":
                return await PowerAsync(connection, "restart", "Restarting. The NAS should answer again in a few minutes.", ct);

            case "shutdown":
                return await PowerAsync(connection, "shutdown", "Shutting down.", ct);

            case "wake":
                if (MacAddress(connection) is not { } mac)
                    return ActionResult.Failed("No MAC address on this connection — add one in its settings, under More settings.");
                await WakeAsync(mac, ct);
                // Deliberately not "the NAS is awake". Nothing acknowledges a magic packet,
                // so claiming success here would be claiming something we cannot know.
                return ActionResult.Done("Magic packet sent. Give it a minute or two to boot.");

            default:
                return ActionResult.Failed($"No QNAP action called “{action.Id}”.");
        }
    }

    /// <summary>
    /// QTS power management. The <c>count</c> parameter is cache-busting rather than
    /// meaningful — the web UI puts a random fraction there — and the request answers
    /// before the machine actually goes anywhere.
    /// </summary>
    private async Task<ActionResult> PowerAsync(Connection connection, string apply, string success, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        var sid = await SessionIdAsync(connection, ct);
        var count = Random.Shared.NextDouble().ToString("0.0000000000000000", CultureInfo.InvariantCulture);
        var url = $"{BaseUrl(connection)}cgi-bin/sys/sysRequest.cgi?subfunc=power_mgmt&count={count}&sid={sid}&apply={apply}";

        using var response = await http.GetAsync(url, ct);
        var body = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : "";

        // The session dies with the machine either way, and an expired one has to be
        // thrown away regardless, so forget it before deciding what happened.
        InvalidateSession(connection);

        if (!response.IsSuccessStatusCode)
            return ActionResult.Failed($"QTS answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");

        // An expired sid is answered with a 200 and authPassed=0, so the request "succeeds"
        // and the NAS does nothing. Reporting a reboot that never happened is the one
        // outcome worse than failing.
        if (RejectedSession(body))
            return ActionResult.Failed("QTS rejected the session. Test the connection, then try again.");

        return ActionResult.Done(success);
    }

    /// <summary>True when QTS answered with an explicit authentication failure.</summary>
    private static bool RejectedSession(string body)
    {
        try
        {
            return XDocument.Parse(body).Descendants("authPassed").FirstOrDefault()?.Value.Trim() == "0";
        }
        catch (System.Xml.XmlException)
        {
            // Power management does not always answer with XML at all. Nothing to read is
            // not evidence of a rejection, and guessing here would fail working reboots.
            return false;
        }
    }

    /// <summary>
    /// A wake-on-LAN magic packet: six 0xFF bytes then the MAC sixteen times. Broadcast
    /// rather than sent to the NAS, because the whole point is that the NAS has no address
    /// at the moment — it is the switch that has to carry this, not IP.
    /// </summary>
    private static async Task WakeAsync(byte[] mac, CancellationToken ct)
    {
        var packet = new byte[6 + 16 * 6];
        packet.AsSpan(0, 6).Fill(0xFF);
        for (var repeat = 0; repeat < 16; repeat++)
            mac.CopyTo(packet, 6 + repeat * 6);

        using var client = new UdpClient { EnableBroadcast = true };
        // Port 9 (discard) is the convention; 7 also works and nothing listens on either.
        await client.SendAsync(packet, new IPEndPoint(IPAddress.Broadcast, 9), ct);
    }

    /// <summary>The configured MAC as six bytes, or null if there isn't a usable one.</summary>
    private static byte[]? MacAddress(Connection connection)
    {
        // Colons, dashes, dots or nothing at all — people paste it from wherever it was shown.
        var digits = new string([.. connection.Settings.Get("mac").Where(Uri.IsHexDigit)]);
        if (digits.Length != 12)
            return null;

        var mac = new byte[6];
        for (var index = 0; index < 6; index++)
            mac[index] = Convert.ToByte(digits.Substring(index * 2, 2), 16);
        return mac;
    }

    /// <summary>
    /// Where this NAS lives, ending in a slash. Public because a plugin building a File
    /// Station URL should not have to reassemble the host, port and scheme itself and get
    /// the https default wrong.
    /// </summary>
    public string BaseUrl(Connection connection)
    {
        var https = connection.Settings.GetBool("https");
        var port = connection.Settings.GetInt("port", https ? 443 : 8080);
        return $"{(https ? "https" : "http")}://{connection.Settings.Get("host")}:{port}/";
    }

    /// <summary>
    /// The QTS session id, logging in if there isn't one. Shared deliberately: File
    /// Station and the management CGI take the same sid, so a plugin that browses files
    /// borrows this session rather than opening a second one against the same account —
    /// QTS counts those, and the second login is what expires the first.
    /// </summary>
    public async Task<string> SessionIdAsync(Connection connection, CancellationToken ct)
    {
        if (_sessions.TryGetValue(connection.Id, out var cached))
            return cached;

        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        var password = Convert.ToBase64String(Encoding.UTF8.GetBytes(connection.Settings.Get("password")));
        var url = $"{BaseUrl(connection)}cgi-bin/authLogin.cgi" +
                  $"?user={Uri.EscapeDataString(connection.Settings.Get("username"))}&pwd={Uri.EscapeDataString(password)}";
        var doc = XDocument.Parse(await http.GetStringAsync(url, ct));

        var passed = doc.Descendants("authPassed").FirstOrDefault()?.Value.Trim();
        var sid = doc.Descendants("authSid").FirstOrDefault()?.Value.Trim();
        if (passed != "1" || string.IsNullOrEmpty(sid))
            throw new InvalidOperationException("Login rejected — check the username and password. Accounts with 2FA enabled cannot use this API.");

        _sessions[connection.Id] = sid;
        return sid;
    }

    /// <summary>
    /// Forgets the cached session so the next call logs in again. Call it when QTS
    /// answers a request of your own with an auth failure — an sid can expire between
    /// two calls that both looked fine a second ago.
    /// </summary>
    public void InvalidateSession(Connection connection) => _sessions.TryRemove(connection.Id, out _);

    private async Task<XDocument> GetXmlAsync(Connection connection, Func<string, string> buildUrl, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        var doc = XDocument.Parse(await http.GetStringAsync(BaseUrl(connection) + buildUrl(await SessionIdAsync(connection, ct)), ct));

        // An expired session answers 200 with authPassed=0; log back in once and retry.
        if (doc.Descendants("authPassed").FirstOrDefault()?.Value.Trim() == "0")
        {
            InvalidateSession(connection);
            doc = XDocument.Parse(await http.GetStringAsync(BaseUrl(connection) + buildUrl(await SessionIdAsync(connection, ct)), ct));
        }
        return doc;
    }

    private static string? Str(XContainer doc, string name)
    {
        var value = doc.Descendants(name).FirstOrDefault()?.Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static double? Num(XContainer doc, string name) =>
        Number(doc.Descendants(name).FirstOrDefault()?.Value);

    private static double? Number(string? raw)
    {
        raw = raw?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        // QTS decorates numbers with units ("42 %", "55 C") depending on the endpoint.
        var digits = new string([.. raw.TakeWhile(c => char.IsDigit(c) || c is '.' or '-')]);
        return double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}
