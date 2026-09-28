using System.Net;
using System.Net.Sockets;

namespace LabbyTwo.Services;

/// <summary>
/// Slows password guessing against the login, per client address and for the install as a
/// whole. Without it the login form would answer as fast as it could be asked, and one
/// password guards everything the dashboard can do — a shell through the terminal plugin,
/// the Docker socket, every stored credential.
///
/// Per address: the first <see cref="Limits.FreeFailures"/> wrong passwords cost nothing,
/// because people mistype. After that each failure shuts that address out for twice as long
/// as the one before — 1 s, 2 s, 4 s … — up to <see cref="Limits.MaxLockout"/>. A correct
/// password clears the address's record.
///
/// Globally: an attacker with many addresses gets five free guesses from each, so there is
/// also a ceiling on failures across every address. Past it, all logins wait
/// <see cref="Limits.GlobalLockout"/> after every further failure, which holds a botnet to
/// about one guess a minute however many addresses it has. The cost is that the owner
/// cannot sign in during such an attack either; an existing session cookie keeps working,
/// and a locked-out login is a far better outcome than a guessed one.
///
/// Kept in memory. LabbyTwo is one process, so there is nothing to share it with, and
/// a restart forgetting the counts only gives an attacker what a restart costs them
/// anyway. The table is bounded: an address is forgotten once it has been quiet for
/// <see cref="Limits.ForgetAfter"/>, and past <see cref="Limits.MaxTracked"/> addresses the
/// quietest go first, so a flood of spoofed or rotated addresses cannot grow it forever.
/// </summary>
public sealed class LoginThrottle(ILogger<LoginThrottle> log, TimeProvider? clock = null, LoginThrottle.Limits? limits = null)
{
    /// <summary>The numbers, all in one place so a test can shrink them.</summary>
    public sealed record Limits
    {
        public int FreeFailures { get; init; } = 5;
        public TimeSpan FirstLockout { get; init; } = TimeSpan.FromSeconds(1);
        public TimeSpan MaxLockout { get; init; } = TimeSpan.FromMinutes(15);
        public int GlobalCeiling { get; init; } = 50;
        public TimeSpan GlobalWindow { get; init; } = TimeSpan.FromMinutes(10);
        public TimeSpan GlobalLockout { get; init; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Longer than <see cref="MaxLockout"/>, so an address kept at the cap never gets
        /// its free attempts back by waiting out one lockout.
        /// </summary>
        public TimeSpan ForgetAfter { get; init; } = TimeSpan.FromHours(1);

        public int MaxTracked { get; init; } = 10_000;
    }

    public enum Outcome
    {
        Succeeded,
        Failed,

        /// <summary>Refused without the password being looked at.</summary>
        Throttled,
    }

    /// <param name="RetryAfter">
    /// How long before this address may try again: after a failure, the lockout it just
    /// earned (zero while it still has free attempts); when throttled, what is left of it.
    /// </param>
    public readonly record struct Result(Outcome Outcome, TimeSpan RetryAfter);

    /// <summary>For the health page: what the throttle has done since LabbyTwo started.</summary>
    public sealed record Status(
        long Failures,
        long Refused,
        int LockedAddresses,
        DateTimeOffset? GlobalLockedUntil,
        DateTimeOffset? LastLockAt,
        string? LastLockAddress);

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset LastFailure;
        public DateTimeOffset LockedUntil;
    }

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Limits _limits = limits ?? new Limits();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _addresses = [];
    private readonly Queue<DateTimeOffset> _recent = new();
    private DateTimeOffset _globalLockedUntil;
    private DateTimeOffset _lastSweep;
    private long _failures;
    private long _refused;
    private DateTimeOffset? _lastLockAt;
    private string? _lastLockAddress;

    /// <summary>Addresses currently remembered. For tests of the eviction.</summary>
    public int Tracked
    {
        get { lock (_gate) return _addresses.Count; }
    }

    /// <summary>
    /// Checks whether <paramref name="client"/> may try, and if so runs
    /// <paramref name="verify"/> and records the result. The check, the verification and
    /// the bookkeeping happen under one lock: otherwise a burst of simultaneous posts would
    /// all pass the check before the first failure was counted, and the limit would only
    /// apply to guesses made one at a time. Verification is two hashes, so holding the lock
    /// across it costs nothing.
    /// </summary>
    public Result Attempt(IPAddress? client, Func<bool> verify)
    {
        var key = KeyFor(client);
        var now = _clock.GetUtcNow();

        lock (_gate)
        {
            _addresses.TryGetValue(key, out var entry);
            var wait = Max(entry is null ? TimeSpan.Zero : entry.LockedUntil - now, _globalLockedUntil - now);
            if (wait > TimeSpan.Zero)
            {
                _refused++;
                // Debug rather than Warning: each lockout is already logged once when it is
                // imposed, and a flood of refused posts would otherwise flood the log too.
                log.LogDebug("Refused a sign-in attempt from {Address}; locked for another {Wait}", key, wait);
                return new Result(Outcome.Throttled, wait);
            }

            if (verify())
            {
                _addresses.Remove(key);
                return new Result(Outcome.Succeeded, TimeSpan.Zero);
            }

            _failures++;
            Sweep(now);

            // Looked up again: the sweep may just have dropped it.
            if (!_addresses.TryGetValue(key, out entry))
            {
                entry = new Entry();
                _addresses[key] = entry;
            }
            // An address quiet for long enough starts again, whether or not a sweep has
            // got round to it yet — the same rule either way.
            else if (now - entry.LastFailure > _limits.ForgetAfter)
            {
                entry.Failures = 0;
            }
            entry.Failures++;
            entry.LastFailure = now;

            var lockout = LockoutAfter(entry.Failures, _limits);
            entry.LockedUntil = now + lockout;
            if (lockout > TimeSpan.Zero)
            {
                _lastLockAt = now;
                _lastLockAddress = key;
                log.LogWarning(
                    "{Failures} failed sign-in attempts from {Address}; refusing it for {Lockout}",
                    entry.Failures, key, lockout);
            }

            _recent.Enqueue(now);
            while (_recent.Count > 0 && now - _recent.Peek() > _limits.GlobalWindow)
                _recent.Dequeue();
            if (_recent.Count > _limits.GlobalCeiling)
            {
                _globalLockedUntil = now + _limits.GlobalLockout;
                lockout = Max(lockout, _limits.GlobalLockout);
                _lastLockAt = now;
                _lastLockAddress = key;
                log.LogWarning(
                    "{Count} failed sign-in attempts in {Window} across all addresses, the last from {Address}; " +
                    "refusing every sign-in for {Lockout}",
                    _recent.Count, _limits.GlobalWindow, key, _limits.GlobalLockout);
            }

            return new Result(Outcome.Failed, lockout);
        }
    }

    public Status Snapshot()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            return new Status(
                _failures,
                _refused,
                _addresses.Values.Count(e => e.LockedUntil > now),
                _globalLockedUntil > now ? _globalLockedUntil : null,
                _lastLockAt,
                _lastLockAddress);
        }
    }

    /// <summary>
    /// Nothing for the first few failures, then doubling from <see cref="Limits.FirstLockout"/>
    /// to the cap. Public and pure so the schedule itself can be tested.
    /// </summary>
    public static TimeSpan LockoutAfter(int failures, Limits limits)
    {
        var over = failures - limits.FreeFailures;
        if (over <= 0)
            return TimeSpan.Zero;
        // Capped before shifting: 2^(over-1) overflows long before anyone gets there, but a
        // determined script might.
        var doublings = Math.Min(over - 1, 40);
        var ticks = limits.FirstLockout.Ticks * (1L << doublings);
        return ticks <= 0 || ticks > limits.MaxLockout.Ticks ? limits.MaxLockout : TimeSpan.FromTicks(ticks);
    }

    /// <summary>
    /// The address an attempt is counted against. An IPv4 client reached over a dual-stack
    /// socket arrives as ::ffff:a.b.c.d and must count as a.b.c.d. An IPv6 client is
    /// counted by its /64: one home connection is usually handed a whole /64, so keying on
    /// the full address would give an attacker 2^64 fresh sets of free attempts.
    /// </summary>
    public static string KeyFor(IPAddress? address)
    {
        if (address is null)
            return "unknown";
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || IPAddress.IsLoopback(address))
            return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    /// <summary>Drops quiet addresses, then the quietest if there are still too many.</summary>
    private void Sweep(DateTimeOffset now)
    {
        // Once a minute is plenty for the age rule; the size rule is checked every time.
        if (now - _lastSweep < TimeSpan.FromMinutes(1) && _addresses.Count < _limits.MaxTracked)
            return;
        _lastSweep = now;

        foreach (var (key, entry) in _addresses)
            if (now - entry.LastFailure > _limits.ForgetAfter && entry.LockedUntil <= now)
                _addresses.Remove(key);

        // One below the cap, leaving room for the address about to be added.
        var excess = _addresses.Count - (_limits.MaxTracked - 1);
        if (excess > 0)
            foreach (var key in _addresses.OrderBy(pair => pair.Value.LastFailure).Take(excess).Select(pair => pair.Key).ToList())
                _addresses.Remove(key);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>"12 seconds", "2 minutes": rounded up, so "try again in" is never early.</summary>
    public static string Describe(TimeSpan wait)
    {
        if (wait <= TimeSpan.FromSeconds(90))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        }
        var minutes = (int)Math.Ceiling(wait.TotalMinutes);
        return $"{minutes} minutes";
    }
}
