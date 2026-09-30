using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>
/// One thing LabbyTwo has noticed about itself, from the first reading that showed it until
/// it has cleared for long enough.
/// </summary>
/// <param name="FirstSeen">The first reading that showed it, in this spell.</param>
public sealed record SelfAlert(string Key, SelfCheck Check, SystemHealth.Level Level, DateTimeOffset FirstSeen)
{
    /// <summary>When it had held for its sustain time and became something to report. Null while it is still only suspected.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Whether the notice that it started reached a channel. Only then is its end announced.</summary>
    public bool Notified { get; init; }

    /// <summary>The first reading that showed it clear, while it waits out its quiet time.</summary>
    public DateTimeOffset? ClearSeen { get; init; }

    public string Title { get; init; } = "";
    public string Body { get; init; } = "";

    public bool Firing => Since is not null;
}

/// <summary>A notice owed that something has cleared, kept until nothing holds it back.</summary>
public sealed record SelfClearNotice(string Key, SelfCheck Check, string Title, string Body, DateTimeOffset At);

/// <summary>
/// Which of LabbyTwo's own problems are current and what is owed about them — the memory
/// behind "sent once when it starts, once when it clears".
///
/// Pure bookkeeping: <see cref="Step"/> is handed each round of readings and the clock, and
/// the service that owns it does the sending and reports back. The rules:
/// <list type="bullet">
/// <item>A reading that holds for its check's sustain time starts the alert. One that clears
/// before then was never reported and is forgotten.</item>
/// <item>A started alert ends once readings have been clear for the check's quiet time; a
/// reading that holds in the meantime restarts the quiet time, so a number hovering at the
/// threshold is one alert, not a stream of them.</item>
/// <item>The notice that it ended is owed only if the one that it started was delivered. If
/// it starts again while that is still owed (held by quiet hours, say), the owed notice is
/// dropped and the alert carries on as already announced — from the reader's side nothing
/// changed.</item>
/// <item>A check turned off forgets its alerts without a word, as a deleted rule does.</item>
/// <item>An unknown reading — just after a restart, before there is enough to judge — changes
/// nothing, so a restart neither announces an old problem again nor declares it over.</item>
/// </list>
/// </summary>
public sealed class SelfWatchLedger
{
    private readonly Dictionary<string, SelfAlert> _alerts = new(StringComparer.Ordinal);
    private readonly List<SelfClearNotice> _owedClears = [];

    public IReadOnlyCollection<SelfAlert> Alerts => [.. _alerts.Values];

    public IReadOnlyList<SelfClearNotice> OwedClears => [.. _owedClears];

    /// <summary>Started alerts whose notice has not reached anyone yet.</summary>
    public IReadOnlyList<SelfAlert> OwedStarts => [.. _alerts.Values.Where(a => a.Firing && !a.Notified).OrderBy(a => a.Since)];

    /// <summary>The keys that have started, which <see cref="SelfWatchRules.Evaluate"/> holds to the lower threshold.</summary>
    public IReadOnlySet<string> FiringKeys => _alerts.Values.Where(a => a.Firing).Select(a => a.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>Applies one round of readings.</summary>
    /// <returns>True when something worth keeping across a restart changed.</returns>
    public bool Step(DateTimeOffset now, IEnumerable<SelfReading> readings, Func<SelfCheck, bool> enabled)
    {
        var changed = false;
        foreach (var reading in readings)
        {
            var key = reading.Key;
            if (!enabled(reading.Check))
            {
                changed |= _alerts.Remove(key) | _owedClears.RemoveAll(n => n.Key == key) > 0;
                continue;
            }

            _alerts.TryGetValue(key, out var alert);
            switch (reading.State)
            {
                case SelfState.Unknown:
                    break;

                case SelfState.Holds:
                    alert = (alert ?? new SelfAlert(key, reading.Check, reading.Level, now)) with
                    {
                        Level = reading.Level,
                        Title = reading.Title,
                        Body = reading.Body,
                        ClearSeen = null,
                    };
                    if (!alert.Firing && now - alert.FirstSeen >= SelfWatchRules.Sustain(reading.Check))
                    {
                        alert = alert with { Since = now };
                        // Back before its end was announced: to whoever reads the notices it
                        // never went away, so it is not announced again.
                        if (_owedClears.RemoveAll(n => n.Key == key) > 0)
                            alert = alert with { Notified = true };
                        changed = true;
                    }
                    _alerts[key] = alert;
                    break;

                case SelfState.Clear when alert is not null:
                    if (!alert.Firing)
                    {
                        _alerts.Remove(key);
                        break;
                    }
                    alert = alert with { ClearSeen = alert.ClearSeen ?? now };
                    if (now - alert.ClearSeen!.Value < SelfWatchRules.ClearAfter(reading.Check))
                    {
                        _alerts[key] = alert;
                        break;
                    }
                    _alerts.Remove(key);
                    if (alert.Notified)
                    {
                        var lasted = alert.Since is { } since && now > since ? $" It lasted {Ago.Duration(now - since)}." : "";
                        _owedClears.Add(new SelfClearNotice(key, reading.Check, reading.ClearTitle, reading.ClearBody + lasted, now));
                    }
                    changed = true;
                    break;
            }
        }
        return changed;
    }

    /// <summary>The start notice reached a channel.</summary>
    public void Delivered(string key)
    {
        if (_alerts.TryGetValue(key, out var alert))
            _alerts[key] = alert with { Notified = true };
    }

    /// <summary>The end notice reached a channel, or is no longer wanted.</summary>
    public void Delivered(SelfClearNotice notice) => _owedClears.Remove(notice);

    // ---------- Across a restart ----------

    private sealed record Stored(List<StoredAlert> Alerts, List<SelfClearNotice> Clears);

    private sealed record StoredAlert(string Key, SelfCheck Check, SystemHealth.Level Level, DateTimeOffset FirstSeen,
        DateTimeOffset Since, bool Notified, string Title, string Body);

    /// <summary>What is kept across a restart: the alerts that have started and the notices still owed. Pending ones start again.</summary>
    public string Save() => JsonSerializer.Serialize(new Stored(
        [.. _alerts.Values.Where(a => a.Firing).Select(a => new StoredAlert(a.Key, a.Check, a.Level, a.FirstSeen, a.Since!.Value,
            a.Notified, a.Title, a.Body))],
        [.. _owedClears]));

    /// <summary>Puts back what <see cref="Save"/> kept. Anything unreadable is dropped: starting afresh is the safe mistake.</summary>
    public void Load(string? saved)
    {
        _alerts.Clear();
        _owedClears.Clear();
        if (string.IsNullOrWhiteSpace(saved))
            return;
        try
        {
            if (JsonSerializer.Deserialize<Stored>(saved) is not { } stored)
                return;
            foreach (var a in stored.Alerts ?? [])
            {
                _alerts[a.Key] = new SelfAlert(a.Key, a.Check, a.Level, a.FirstSeen)
                {
                    Since = a.Since, Notified = a.Notified, Title = a.Title, Body = a.Body,
                };
            }
            _owedClears.AddRange(stored.Clears ?? []);
        }
        catch (JsonException)
        {
        }
    }
}
