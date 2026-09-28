using System.Security.Cryptography;
using System.Text.Json;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace LabbyTwo.Services.Offsite;

/// <summary>What happened the last time one destination was tried. Kept across restarts.</summary>
public sealed record DestinationStatus
{
    public DateTimeOffset? LastRunAt { get; init; }
    public bool Ok { get; init; }
    public string Message { get; init; } = "";

    public DateTimeOffset? LastSuccessAt { get; init; }
    public long LastSize { get; init; }
    public string LastName { get; init; } = "";

    public DateTimeOffset? LastFailureAt { get; init; }

    /// <summary>
    /// Whether the current run of failures has been announced. What makes a destination
    /// that fails every night alert once rather than every night — and, because it is only
    /// set once an alert actually went out, what makes one held by quiet hours go out the
    /// next night instead of never.
    /// </summary>
    public bool FailureAnnounced { get; init; }
}

/// <summary>
/// Off-site settings, in <c>app_settings</c> like everything else the UI changes: the list
/// of destinations as JSON, the passphrase, and the last result for each destination.
///
/// Secrets — each S3 secret key and the passphrase — are encrypted with the same
/// DataProtection keyring as connection credentials, under their own purpose. That keeps
/// them out of the plain database copy that goes off-site when no passphrase is set.
/// </summary>
public sealed class OffsiteSettingsStore(AppSettingsStore settings, IDataProtectionProvider protection)
{
    public const string DestinationsKey = "offsite_destinations";
    public const string PassphraseKey = "offsite_passphrase";
    public const string StatusKey = "offsite_status";

    private const string SecretPrefix = "enc:";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly IDataProtector _protector = protection.CreateProtector("LabbyTwo.OffsiteSecrets");

    /// <summary>
    /// Every destination, with secrets decrypted. A secret that no longer decrypts — the
    /// keyring was lost or replaced — comes back empty, so the destination reports "both
    /// keys are needed" instead of signing with garbage.
    /// </summary>
    public async Task<IReadOnlyList<OffsiteDestination>> DestinationsAsync(CancellationToken ct = default)
    {
        var raw = await settings.GetAsync(DestinationsKey, "", ct);
        if (raw.Length == 0)
            return [];

        List<OffsiteDestination>? stored;
        try
        {
            stored = JsonSerializer.Deserialize<List<OffsiteDestination>>(raw, Json);
        }
        catch (JsonException)
        {
            return [];
        }

        return [.. (stored ?? []).Select(d => d with { SecretKey = Unprotect(d.SecretKey) })];
    }

    public async Task SaveDestinationsAsync(IEnumerable<OffsiteDestination> destinations, CancellationToken ct = default)
    {
        var stored = destinations.Select(d => d with { SecretKey = Protect(d.SecretKey) }).ToList();
        await settings.SaveAsync(DestinationsKey, JsonSerializer.Serialize(stored, Json), ct);
    }

    public async Task<string> PassphraseAsync(CancellationToken ct = default) =>
        Unprotect(await settings.GetAsync(PassphraseKey, "", ct));

    public Task SavePassphraseAsync(string passphrase, CancellationToken ct = default) =>
        settings.SaveAsync(PassphraseKey, Protect(passphrase), ct);

    public async Task<IReadOnlyDictionary<string, DestinationStatus>> StatusesAsync(CancellationToken ct = default)
    {
        var raw = await settings.GetAsync(StatusKey, "", ct);
        if (raw.Length == 0)
            return new Dictionary<string, DestinationStatus>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, DestinationStatus>>(raw, Json)
                   ?? new Dictionary<string, DestinationStatus>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, DestinationStatus>();
        }
    }

    public Task SaveStatusesAsync(IReadOnlyDictionary<string, DestinationStatus> statuses, CancellationToken ct = default) =>
        settings.SaveAsync(StatusKey, JsonSerializer.Serialize(statuses, Json), ct);

    private string Protect(string value) =>
        value.Length == 0 ? "" : SecretPrefix + _protector.Protect(value);

    private string Unprotect(string value)
    {
        if (!value.StartsWith(SecretPrefix, StringComparison.Ordinal))
            return "";
        try
        {
            return _protector.Unprotect(value[SecretPrefix.Length..]);
        }
        catch (CryptographicException)
        {
            return "";
        }
    }
}
