using System.Net;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.HttpOverrides;

namespace LabbyTwo.Services;

/// <summary>
/// Decides whose X-Forwarded-For (or CF-Connecting-IP) to believe. The client address it
/// produces is what the login throttle counts failures against, so believing the header
/// from anyone would let an attacker name a fresh address with every guess and never be
/// slowed — or name yours and lock you out. Only the proxies listed in
/// <see cref="LabbyOptions.ProxySettings.TrustedProxies"/>, plus loopback as ASP.NET
/// already does, are believed; everyone else is taken to be exactly who the socket says.
/// </summary>
public static class ForwardedHeadersSetup
{
    /// <summary>Applies the settings, and returns every entry it could not read.</summary>
    public static IReadOnlyList<string> Apply(ForwardedHeadersOptions forwarded, LabbyOptions.ProxySettings settings)
    {
        forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        if (!string.IsNullOrWhiteSpace(settings.ClientIpHeader))
            forwarded.ForwardedForHeaderName = settings.ClientIpHeader.Trim();

        var invalid = new List<string>();
        var any = false;
        foreach (var entry in settings.TrustedProxies.Split([',', ' ', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Contains('/') && System.Net.IPNetwork.TryParse(entry, out var network))
            {
                forwarded.KnownIPNetworks.Add(network);
                any = true;
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                forwarded.KnownProxies.Add(address);
                any = true;
            }
            else
            {
                invalid.Add(entry);
            }
        }

        // With proxies named, walk back through every one of them — Cloudflare, then
        // cloudflared, then perhaps Caddy — and stop at the first address that is not one of
        // ours. That address is the client. Left at the default of one hop, a second proxy
        // in the chain would be taken for the client and every visitor would share it.
        if (any)
            forwarded.ForwardLimit = null;

        return invalid;
    }
}
