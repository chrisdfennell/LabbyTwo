namespace LabbyTwo.Services;

/// <summary>
/// Safe mode: this browser, for this visit, gets the dashboard without the owner's custom CSS.
///
/// Custom CSS can hide anything, including the Appearance page and the switch that turns it
/// off — <c>body { display: none }</c> is one line. So there has to be a way in that does not
/// depend on the page being usable: add <c>?safe=1</c> to any address. That sets a session
/// cookie (gone when the browser closes), and every page this browser opens until then leaves
/// the custom block empty and says so in a banner. <c>?safe=0</c> ends it early.
///
/// A cookie rather than the query string alone, because the query is gone after the first
/// link clicked, and the fix is usually two pages away from where the problem showed. Per
/// browser rather than a setting, because the wall in the hall should keep looking right
/// while somebody repairs the stylesheet on a laptop.
///
/// Works signed out too (the login page has a link), since the cookie says nothing about who
/// anybody is and only ever takes something away.
/// </summary>
public static class SafeMode
{
    public const string QueryKey = "safe";
    public const string CookieName = "labbytwo.safe";
    private const string ItemKey = "labbytwo.safe";

    /// <summary>
    /// Reads the request — a <c>?safe=</c> in the address wins over the cookie and updates it —
    /// and remembers the answer for the rest of the request.
    /// </summary>
    public static bool Resolve(HttpContext context)
    {
        bool on;
        var asked = context.Request.Query[QueryKey].ToString().Trim().ToLowerInvariant();
        if (asked is "1" or "true" or "on" or "yes")
        {
            on = true;
            context.Response.Cookies.Append(CookieName, "1", new CookieOptions
            {
                // No expiry: a session cookie, so closing the browser ends safe mode.
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps,
                Path = "/",
            });
        }
        else if (asked is "0" or "false" or "off" or "no")
        {
            on = false;
            context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
        }
        else
        {
            on = context.Request.Cookies.ContainsKey(CookieName);
        }

        context.Items[ItemKey] = on;
        return on;
    }

    /// <summary>Whether this request is in safe mode. Null — no request, as in an interactive render — is not.</summary>
    public static bool IsOn(HttpContext? context)
    {
        if (context is null)
            return false;
        return context.Items.TryGetValue(ItemKey, out var known) && known is bool on ? on : Resolve(context);
    }

    /// <summary>Resolves safe mode for every request early, so a redirect to the login page still carries the cookie.</summary>
    public static IApplicationBuilder UseSafeMode(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            Resolve(context);
            return next(context);
        });
}
