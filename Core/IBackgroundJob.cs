namespace LabbyTwo.Core;

/// <summary>
/// Work that runs on a timer rather than because somebody opened a page. The sixth
/// extension point, and the one that was missing for the longest: until this existed a
/// plugin could describe things and render things, but could not *do* anything unless a
/// browser was pointed at it — so a plugin that wanted a nightly tidy-up had to disguise
/// it as a probe.
///
/// Deliberately not <c>IHostedService</c>. A hosted service is a lifetime to manage, and a
/// plugin that hangs in <c>StartAsync</c> hangs the whole app; this is a method with an
/// interval, run by <see cref="Services.BackgroundJobRunner"/>, where a job that throws or
/// overruns is that job's problem alone.
/// </summary>
public interface IBackgroundJob
{
    /// <summary>Stable key, and what the Settings page calls this. e.g. "drop-cleanup".</summary>
    string Name { get; }

    /// <summary>
    /// How often to run. Clamped to at least a minute — anything that wants to be faster
    /// than that is really reacting to something, and should be triggered by it instead.
    /// </summary>
    TimeSpan Interval { get; }

    /// <summary>
    /// Whether to run once at startup as well as on the interval. False for anything
    /// expensive: a dozen plugins all doing their daily sweep during boot is how a
    /// dashboard comes up slowly and nobody knows why.
    /// </summary>
    bool RunAtStartup => false;

    /// <summary>
    /// One run. Throwing is survivable — it is logged, shown on the Settings page, and the
    /// job is tried again next interval — but a job that throws every time is a job nobody
    /// notices is broken, so say something useful in the message.
    /// </summary>
    Task RunAsync(CancellationToken ct);
}
