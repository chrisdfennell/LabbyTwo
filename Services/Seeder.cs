using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Creates a starter layout on request. Deliberately contains nothing about anybody's
/// network — a clock, a status roll-up and an empty bookmark card — so a fresh install
/// looks like a dashboard without pretending to know what is on the LAN.
/// </summary>
public sealed class Seeder(ConfigStore config)
{
    public async Task<string> CreateStarterLayoutAsync(CancellationToken ct = default)
    {
        var dashboard = new Tab
        {
            Slug = await config.UniqueSlugAsync("Dashboard", ct: ct),
            Name = "Dashboard",
            Icon = "🏠",
            Kind = TabKinds.Grid,
            Sort = 0,
        };
        await config.SaveTabAsync(dashboard, ct);

        var sort = 0;
        async Task AddAsync(string type, string title, int width, SettingsBag? settings = null) =>
            await config.SaveWidgetAsync(new Widget
            {
                TabId = dashboard.Id,
                Type = type,
                Title = title,
                Width = width,
                Sort = sort++,
                Settings = settings ?? new SettingsBag(),
            }, ct);

        await AddAsync("greeting", "", 6);
        await AddAsync("clock", "", 3);
        await AddAsync("status-summary", "Services", 3);
        await AddAsync("search", "", 12);

        await AddAsync("markdown", "Getting started", 6, new SettingsBag
        {
            ["content"] =
                """
                ### You're set up

                1. Add a **connection** for each thing on your network — a NAS, Plex, or
                   just a URL with the *Web service* type.
                2. Come back here, hit **Edit layout**, and drag cards around or add
                   widgets bound to those connections.
                3. Add more **tabs** for anything else: a grid, an embedded web UI, or notes.

                Already have a dashboard elsewhere? **Settings → Import** reads Homer,
                Homepage, Heimdall and browser bookmarks.

                Delete this card whenever you like.
                """,
        });

        // An empty bookmarks card rather than none: it is the thing most people want
        // first, and an existing card is easier to fill in than a picker is to find.
        await AddAsync("links", "Bookmarks", 6, new SettingsBag
        {
            ["links"] = LinkRow.Serialize(
            [
                new LinkRow("", "Edit this card to add your own", "https://github.com"),
            ]),
        });

        await config.SaveTabAsync(new Tab
        {
            Slug = await config.UniqueSlugAsync("Notes", ct: ct),
            Name = "Notes",
            Icon = "📝",
            Kind = TabKinds.Notes,
            Sort = 1,
        }, ct);

        return dashboard.Slug;
    }
}
