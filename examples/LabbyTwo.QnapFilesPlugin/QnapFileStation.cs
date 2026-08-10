using System.Text;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Providers;

namespace LabbyTwo.QnapFilesPlugin;

/// <summary>
/// QNAP's File Station API — <c>cgi-bin/filemanager/utilRequest.cgi</c> — on top of the
/// session <see cref="QnapProvider"/> already holds. Borrowing that session is the point:
/// File Station and the management CGI take the same sid, and a second login against the
/// same account is what expires the first, so a plugin that logged in for itself would
/// quietly fight the health monitor for the connection it is sitting next to.
/// </summary>
public sealed class QnapFileStation(IHttpClientFactory httpFactory, QnapProvider qnap)
{
    /// <param name="Modified">Null when QTS did not report a timestamp, which some shares do not.</param>
    public sealed record Entry(string Name, bool IsFolder, long SizeBytes, DateTimeOffset? Modified);

    /// <summary>
    /// The top-level shared folders. This is a different call from a directory listing —
    /// the root of a QNAP is a list of shares, not a folder — which is why the tab treats
    /// an empty path as its own case rather than listing "/".
    /// </summary>
    public async Task<IReadOnlyList<Entry>> SharesAsync(Connection nas, CancellationToken ct)
    {
        using var document = await GetJsonAsync(nas,
            sid => $"cgi-bin/filemanager/utilRequest.cgi?func=get_tree&is_iso=0&node=share_root&sid={sid}", ct);

        var shares = new List<Entry>();
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in document.RootElement.EnumerateArray())
            {
                var name = node.TryGetProperty("text", out var text) ? text.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name))
                    shares.Add(new Entry(name, true, 0, null));
            }
        }
        return [.. shares.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<IReadOnlyList<Entry>> ListAsync(Connection nas, string path, CancellationToken ct)
    {
        using var document = await GetJsonAsync(nas,
            sid => "cgi-bin/filemanager/utilRequest.cgi?func=get_list&is_iso=0&list_mode=all" +
                   $"&limit=1000&start=0&sort=filename&dir=ASC&path={Uri.EscapeDataString(path)}&sid={sid}", ct);

        var entries = new List<Entry>();
        if (document.RootElement.TryGetProperty("datas", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                var name = row.TryGetProperty("filename", out var filename) ? filename.GetString() : null;
                if (string.IsNullOrEmpty(name) || name is "." or "..")
                    continue;

                entries.Add(new Entry(
                    name,
                    Number(row, "isfolder") == 1,
                    (long)Number(row, "filesize"),
                    Number(row, "epochmt") is > 0 and var epoch
                        ? DateTimeOffset.FromUnixTimeSeconds((long)epoch).ToLocalTime()
                        : null));
            }
        }

        return [.. entries
            .OrderByDescending(e => e.IsFolder)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Opens a file for streaming. QTS honours Range requests (206 with a Content-Range),
    /// so passing the browser's Range header straight through is what makes seeking in a
    /// video and resuming an interrupted download work — the whole reason this plugin
    /// needs a real endpoint rather than a component.
    /// </summary>
    public async Task<HttpResponseMessage> OpenDownloadAsync(
        Connection nas, string folder, string name, string? range, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.TransferClientName);

        async Task<HttpResponseMessage> SendAsync(CancellationToken token)
        {
            var url = qnap.BaseUrl(nas) +
                      "cgi-bin/filemanager/utilRequest.cgi?func=download&isfolder=0&source_total=1" +
                      $"&source_path={Uri.EscapeDataString(folder)}&source_file={Uri.EscapeDataString(name)}" +
                      $"&sid={await qnap.SessionIdAsync(nas, token)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(range))
                request.Headers.TryAddWithoutValidation("Range", range);
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }

        var response = await SendAsync(ct);

        // An expired sid does not come back as 401 here: File Station answers 200 with a
        // JSON error where the file should be. Detect that by content type and retry once
        // with a fresh login, which is the difference between "download failed" and a
        // 12-byte file full of {"status":2}.
        if (LooksLikeJsonError(response))
        {
            response.Dispose();
            qnap.InvalidateSession(nas);
            response = await SendAsync(ct);
        }

        response.EnsureSuccessStatusCode();
        return response;
    }

    /// <summary>Uploads into <paramref name="folder"/>, replacing a file of the same name.</summary>
    public async Task UploadAsync(Connection nas, string folder, string name, Stream content, CancellationToken ct)
    {
        var url = qnap.BaseUrl(nas) +
                  "cgi-bin/filemanager/utilRequest.cgi?func=upload&type=standard&overwrite=1" +
                  $"&dest_path={Uri.EscapeDataString(folder)}&progress={Uri.EscapeDataString(name)}" +
                  $"&sid={await qnap.SessionIdAsync(nas, ct)}";

        // QTS's CGI multipart parser is fussy in three ways at once: it needs a
        // Content-Length (so no chunked encoding, so no non-seekable browser stream), it
        // wants a quoted filename with no RFC 5987 filename*, and Content-Disposition has
        // to be the part's first header. .NET's MultipartFormDataContent breaks all three,
        // and QTS then answers success while writing nothing at all — which is a
        // memorable afternoon. So spool the whole body, envelope included, and send it
        // verbatim.
        var boundary = "----labbytwo" + Guid.NewGuid().ToString("n");
        var safeName = name.Replace("\"", "_");
        var spoolPath = Path.Combine(Path.GetTempPath(), $"labbytwo-upload-{Guid.NewGuid():n}");

        try
        {
            await using (var spool = File.Create(spoolPath))
            {
                await spool.WriteAsync(Encoding.UTF8.GetBytes(
                    $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{safeName}\"\r\n" +
                    "Content-Type: application/octet-stream\r\n\r\n"), ct);
                await content.CopyToAsync(spool, ct);
                await spool.WriteAsync(Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n"), ct);
            }

            await using var body = File.OpenRead(spoolPath);
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StreamContent(body) };
            request.Content.Headers.TryAddWithoutValidation(
                "Content-Type", $"multipart/form-data; boundary={boundary}");

            var http = httpFactory.CreateClient(ProviderHttp.TransferClientName);
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            EnsureSucceeded(await response.Content.ReadAsStringAsync(ct), "upload");
        }
        finally
        {
            // Best effort: a temp file left behind is untidy, but failing the upload
            // because the cleanup failed would be worse.
            try { File.Delete(spoolPath); } catch (IOException) { }
        }
    }

    public async Task CreateFolderAsync(Connection nas, string parent, string name, CancellationToken ct)
    {
        using var document = await GetJsonAsync(nas,
            sid => $"cgi-bin/filemanager/utilRequest.cgi?func=createdir&dest_path={Uri.EscapeDataString(parent)}" +
                   $"&dest_folder={Uri.EscapeDataString(name)}&sid={sid}", ct);
        EnsureSucceeded(document.RootElement, "create folder");
    }

    public async Task RenameAsync(Connection nas, string path, string oldName, string newName, CancellationToken ct)
    {
        using var document = await GetJsonAsync(nas,
            sid => $"cgi-bin/filemanager/utilRequest.cgi?func=rename&path={Uri.EscapeDataString(path)}" +
                   $"&source_name={Uri.EscapeDataString(oldName)}&dest_name={Uri.EscapeDataString(newName)}&sid={sid}", ct);
        EnsureSucceeded(document.RootElement, "rename");
    }

    /// <summary>Deletes a file or folder. QTS moves it to the share's recycle bin when that is enabled.</summary>
    public async Task DeleteAsync(Connection nas, string path, string name, CancellationToken ct)
    {
        using var document = await GetJsonAsync(nas,
            sid => $"cgi-bin/filemanager/utilRequest.cgi?func=delete&path={Uri.EscapeDataString(path)}" +
                   $"&file_name={Uri.EscapeDataString(name)}&file_total=1&sid={sid}", ct);
        EnsureSucceeded(document.RootElement, "delete");
    }

    private async Task<JsonDocument> GetJsonAsync(
        Connection nas, Func<string, string> buildUrl, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        var body = await http.GetStringAsync(qnap.BaseUrl(nas) + buildUrl(await qnap.SessionIdAsync(nas, ct)), ct);

        if (!LooksAuthorized(body))
        {
            qnap.InvalidateSession(nas);
            body = await http.GetStringAsync(qnap.BaseUrl(nas) + buildUrl(await qnap.SessionIdAsync(nas, ct)), ct);
        }

        return JsonDocument.Parse(body);
    }

    /// <summary>An expired sid answers {"status": 2 | 3}, or an XML login page on older firmware.</summary>
    private static bool LooksAuthorized(string body)
    {
        if (body.TrimStart().StartsWith('<'))
            return false;

        try
        {
            using var document = JsonDocument.Parse(body);
            return !(document.RootElement.ValueKind == JsonValueKind.Object
                     && document.RootElement.TryGetProperty("status", out var status)
                     && status.ValueKind == JsonValueKind.Number
                     && status.GetInt32() is 2 or 3);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikeJsonError(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType is "application/json" or "text/html";

    private static void EnsureSucceeded(string body, string operation)
    {
        using var document = JsonDocument.Parse(body);
        EnsureSucceeded(document.RootElement, operation);
    }

    /// <summary>
    /// File Station signals success as {"status": 1}; anything else is a code. The two
    /// worth naming are the two people actually hit — a read-only account, and a name
    /// that already exists.
    /// </summary>
    private static void EnsureSucceeded(JsonElement root, string operation)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.Number)
            return;

        var code = status.GetInt32();
        if (code == 1)
            return;

        throw new InvalidOperationException(code switch
        {
            2 or 3 => "File Station rejected the session. Test the QNAP connection and try again.",
            4 => $"File Station refused the {operation} — permission denied. Does this account have write access here?",
            33 => "Something with that name is already there.",
            _ => $"File Station could not {operation} (status {code}).",
        });
    }

    /// <summary>File Station returns numbers as numbers or as strings, depending on the firmware.</summary>
    private static double Number(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0,
        };
    }
}
