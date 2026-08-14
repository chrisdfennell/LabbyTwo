namespace LabbyTwo.Core;

/// <summary>
/// Something a provider can <em>do</em> to the thing it monitors, rather than ask it.
///
/// Everything else here reports; a dashboard you can only read is one you leave to go and
/// do the actual work somewhere else. The Action card already offered a button, but it is
/// a bare HTTP call that knows nothing about a connection — so "restart the NAS" meant
/// typing the NAS's password into a widget, next to the connection that already had it.
/// An action runs on the provider, with the credentials the connection was configured
/// with, which is the whole difference.
///
/// Declared like a <see cref="FieldSpec"/> and rendered by shared UI, so a provider that
/// wants a button writes a record and a method and no markup at all.
/// </summary>
public sealed record ProviderAction(string Id, string Label, string Icon = "")
{
    /// <summary>One line under the button. Say what happens, not what it is called.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Ask before running. On by default, and deliberately: an action button lives on a
    /// dashboard, and a dashboard is a wall tablet a child can lean on. Turn it off only
    /// for something whose worst case is having to press it again.
    /// </summary>
    public bool Confirms { get; init; } = true;

    /// <summary>What the confirmation says. Falls back to a generic warning.</summary>
    public string? ConfirmMessage { get; init; }

    /// <summary>
    /// Renders in red, and cannot skip its confirmation whatever <see cref="Confirms"/>
    /// says. For the ones that end with something switched off.
    /// </summary>
    public bool Dangerous { get; init; }

    /// <summary>
    /// Asked for before running — how many minutes to disable blocking, which container to
    /// restart. Same specs the connection form uses, rendered by the same component, and
    /// handed back to <see cref="IConnectionProvider.RunActionAsync"/> as a settings bag.
    /// </summary>
    public IReadOnlyList<FieldSpec> Fields { get; init; } = [];

    /// <summary>
    /// How long the thing is expected to be unreachable afterwards. A reboot you asked for
    /// is not an outage, and the monitor has no way to tell the difference — so the runner
    /// silences the connection for this long rather than paging you about a machine you
    /// just told to go away. Null means the action does not interrupt anything.
    /// </summary>
    public TimeSpan? Disrupts { get; init; }

    public bool NeedsConfirming => Confirms || Dangerous;
}

/// <summary>What happened. Providers should return one of these rather than throw.</summary>
public sealed record ActionResult(bool Ok, string Message)
{
    public static ActionResult Done(string message = "Done.") => new(true, message);
    public static ActionResult Failed(string message) => new(false, message);
}
