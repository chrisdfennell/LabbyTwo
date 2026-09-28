using System.Globalization;
using System.Text.RegularExpressions;

namespace LabbyTwo.Services.Offsite;

/// <summary>
/// Somewhere the nightly backup is copied to that is not the machine it protects.
///
/// One record for both kinds rather than a class hierarchy, because it is stored as JSON
/// in one setting and edited by one form, and a folder simply leaves the S3 fields empty.
/// </summary>
public sealed record OffsiteDestination
{
    public const string FolderKind = "folder";
    public const string S3Kind = "s3";

    public string Id { get; init; } = Guid.NewGuid().ToString("n")[..12];
    public string Name { get; init; } = "";
    public string Kind { get; init; } = FolderKind;
    public bool Enabled { get; init; } = true;

    /// <summary>Folder: a path inside the container — a mounted share or disk.</summary>
    public string Path { get; init; } = "";

    /// <summary>S3: blank means AWS itself, in <see cref="Region"/>.</summary>
    public string Endpoint { get; init; } = "";
    public string Region { get; init; } = "";
    public string Bucket { get; init; } = "";
    public string Prefix { get; init; } = "labbytwo";
    public string AccessKey { get; init; } = "";

    /// <summary>In memory only in the clear. Stored encrypted — see <see cref="OffsiteSettingsStore"/>.</summary>
    public string SecretKey { get; init; } = "";

    /// <summary>
    /// bucket-in-the-path rather than bucket-in-the-hostname. MinIO and most self-hosted
    /// stores want it; AWS, B2, R2 and Wasabi accept the default.
    /// </summary>
    public bool PathStyle { get; init; }

    /// <summary>How many to keep, in <see cref="KeepUnit"/>. 0 keeps everything — see <see cref="Retention"/>.</summary>
    public int Keep { get; init; } = 14;
    public RetentionUnit KeepUnit { get; init; } = RetentionUnit.Copies;

    public bool IsS3 => Kind == S3Kind;

    /// <summary>Where it goes, in a few words, for the list on the Settings page.</summary>
    public string Describe() => IsS3
        ? $"s3://{Bucket}/{NormalisedPrefix}" + (Endpoint.Length > 0 ? $" at {EndpointHost}" : $" ({(Region.Length > 0 ? Region : "us-east-1")})")
        : Path;

    public string NormalisedPrefix => Prefix.Trim().Trim('/') is { Length: > 0 } p ? p + "/" : "";

    private string EndpointHost => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) ? uri.Authority : Endpoint;

    /// <summary>What is wrong with it, or null. Checked on save, so a typo is caught at the form, not at 3am.</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "Give it a name — it is what an alert calls it.";
        if (Keep < 0)
            return "Keep cannot be negative. 0 keeps everything.";

        if (!IsS3)
        {
            if (string.IsNullOrWhiteSpace(Path))
                return "A folder needs a path.";
            if (!System.IO.Path.IsPathRooted(Path))
                return "Use an absolute path, like /backups/nas — a relative one would land inside the container.";
            return null;
        }

        if (Endpoint.Length > 0
            && (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            return "The endpoint is a URL like https://s3.us-west-004.backblazeb2.com — or blank for AWS.";
        if (string.IsNullOrWhiteSpace(Bucket))
            return "Which bucket?";
        if (string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey))
            return "Both the access key and the secret key are needed.";
        return null;
    }
}

public enum RetentionUnit
{
    Copies,
    Days,
}

/// <summary>
/// Which old copies to delete. Pure, so the one piece of this feature that removes things
/// can be tested without a bucket or a share.
///
/// Only names this app writes are ever considered — labbytwo-YYYY-MM-DD with .db or
/// .l2backup — so pointing a destination at a folder or prefix with other things in it
/// cannot cost anybody their other things. Dates come from the name, not the file's time,
/// which a copy between shares or a bucket's replication would have changed.
/// </summary>
public static partial class Retention
{
    [GeneratedRegex(@"^labbytwo-(\d{4}-\d{2}-\d{2})\.(db|l2backup)$")]
    private static partial Regex BackupName();

    /// <summary>The date in one of our names, or null if it is not one of ours.</summary>
    public static DateOnly? DateOf(string fileName)
    {
        var match = BackupName().Match(fileName);
        return match.Success && DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>
    /// Of <paramref name="names"/> (file names, no folders), the ones to delete.
    ///
    /// The newest is never deleted, whatever the rule says: "keep 7 days" on a NAS that has
    /// been off for a fortnight would otherwise delete the only copy there is.
    /// </summary>
    public static IReadOnlyList<string> ToDelete(
        IEnumerable<string> names, int keep, RetentionUnit unit, DateOnly today)
    {
        if (keep <= 0)
            return [];

        var ours = names
            .Select(name => (Name: name, Date: DateOf(name)))
            .Where(item => item.Date is not null)
            .OrderByDescending(item => item.Date)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToList();

        if (ours.Count == 0)
            return [];

        var newest = ours[0];
        var rest = ours.Skip(1);

        IEnumerable<(string Name, DateOnly? Date)> doomed = unit switch
        {
            // By date rather than by file: a day with both a .db and a .l2backup — the
            // day a passphrase was set — is one copy, not two.
            RetentionUnit.Copies => rest.Where(item =>
                ours.Select(o => o.Date).Distinct().TakeWhile(d => d != item.Date).Count() >= keep),
            _ => rest.Where(item => item.Date!.Value < today.AddDays(-keep + 1)),
        };

        return [.. doomed.Select(item => item.Name)];
    }
}
