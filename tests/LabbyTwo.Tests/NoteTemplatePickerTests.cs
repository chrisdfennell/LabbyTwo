#pragma warning disable BL0006 // The interactive test renderer reads its own render tree to find what to change.
using System.Net;
using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>Changing a form field, for the render tests that fill one in.</summary>
internal sealed partial class InteractiveRenderer
{
    /// <summary>
    /// Sends the change a browser would to the element with this id — a select chosen, a
    /// box ticked. Throws when there is no such element with a change handler.
    /// </summary>
    public Task ChangeAsync(string id, object? value, string eventName = "onchange") => Dispatcher.InvokeAsync(async () =>
    {
        var handler = FindChange(_root, id, eventName) ?? throw new InvalidOperationException($"Nothing with id “{id}” takes {eventName}.");
        await DispatchEventAsync(handler, new EventFieldInfo { FieldValue = value! }, new ChangeEventArgs { Value = value });
    });

    private ulong? FindChange(int componentId, string id, string eventName)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames.Array[i];
            if (frame.FrameType == RenderTreeFrameType.Component)
            {
                if (FindChange(frame.ComponentId, id, eventName) is { } inner)
                    return inner;
                continue;
            }
            if (frame.FrameType != RenderTreeFrameType.Element)
                continue;
            var matches = false;
            ulong handler = 0;
            for (var a = i + 1; a < frames.Count && frames.Array[a].FrameType == RenderTreeFrameType.Attribute; a++)
            {
                var attribute = frames.Array[a];
                if (attribute.AttributeName == "id" && Equals(attribute.AttributeValue, id))
                    matches = true;
                if (attribute.AttributeName == eventName && attribute.AttributeEventHandlerId != 0)
                    handler = attribute.AttributeEventHandlerId;
            }
            if (matches && handler != 0)
                return handler;
        }
        return null;
    }
}

/// <summary>
/// "New note from a template…" drawn by the real component against a real database:
/// choosing a built-in template and a connection writes the note with that connection's
/// name safely inside every shortcode, and a saved template is used exactly as written.
/// </summary>
public sealed class NoteTemplatePickerTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly InteractiveRenderer _renderer;
    private NoteDraft? _chosen;

    public NoteTemplatePickerTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);
        services.AddSingleton<IEnumerable<IConnectionProvider>>([]);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<CapacityForecasts>();
        services.AddSingleton<MetricBaselines>();
        services.AddSingleton<MetricAlertService>();
        services.AddSingleton<LatestReadings>();
        services.AddSingleton<Offload>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<ChangeStore>();
        services.AddSingleton<IncidentStore>();
        services.AddSingleton<ProbableCauses>();
        services.AddSingleton<NotesStore>();
        services.AddSingleton<IncidentWriteUps>();
        services.AddSingleton<NoteTemplateStore>();
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        _renderer = new InteractiveRenderer(_services);
    }

    public async ValueTask DisposeAsync()
    {
        await _renderer.DisposeAsync();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private Task RenderAsync() =>
        _renderer.RenderAsync<NoteTemplatePicker>(new Dictionary<string, object?>
        {
            [nameof(NoteTemplatePicker.OnChosen)] = EventCallback.Factory.Create<NoteDraft>(this, draft => _chosen = draft),
        });

    [Fact]
    public async Task ARunbookIsWrittenForTheChosenConnection()
    {
        var odd = new Connection { Provider = "http", Name = "NAS}} {{button: Router / reboot" };
        await Get<ConfigStore>().SaveConnectionAsync(odd);

        await RenderAsync();
        await _renderer.WaitForAsync("Runbook for a connection");
        await _renderer.ClickAsync("Runbook for a connection");
        await _renderer.ChangeAsync("template-connection", odd.Id);
        await _renderer.ClickAsync("Start the note");

        Assert.NotNull(_chosen);
        Assert.Equal("NAS}} {{button: Router / reboot runbook", _chosen.Title);
        Assert.Contains("{{status: \"NAS}} {{button: Router / reboot\"}}", _chosen.Markdown);
        Assert.Contains("{{if down: \"NAS}} {{button: Router / reboot\"}}", _chosen.Markdown);
        Assert.DoesNotContain(Shortcodes.Find(_chosen.Markdown), f => f.Code.Kind == "button");
    }

    [Fact]
    public async Task AnOverviewNeedsAtLeastOneConnectionTicked()
    {
        var nas = new Connection { Provider = "http", Name = "NAS" };
        var plex = new Connection { Provider = "http", Name = "Plex" };
        await Get<ConfigStore>().SaveConnectionAsync(nas);
        await Get<ConfigStore>().SaveConnectionAsync(plex);

        await RenderAsync();
        await _renderer.WaitForAsync("Service overview");
        await _renderer.ClickAsync("Service overview");
        await _renderer.ClickAsync("Start the note");
        Assert.Null(_chosen);

        await _renderer.ChangeAsync($"template-pick-{plex.Id}", true);
        await _renderer.ClickAsync("Start the note");
        Assert.NotNull(_chosen);
        Assert.Contains("{{status: Plex}}", _chosen.Markdown);
        Assert.DoesNotContain("{{status: NAS}}", _chosen.Markdown);
    }

    [Fact]
    public async Task ASavedTemplateIsUsedExactlyAsWritten()
    {
        await Get<NoteTemplateStore>().SaveAsync(null, "Weekly check", "Week of {{today}}", "- [ ] {{status: NAS}}\n[[Plex runbook]]");

        await RenderAsync();
        var html = WebUtility.HtmlDecode(await _renderer.WaitForAsync("Weekly check"));
        Assert.Contains("Your templates", html);
        await _renderer.ClickAsync("Use");

        Assert.Equal(new NoteDraft("Week of {{today}}", "- [ ] {{status: NAS}}\n[[Plex runbook]]"), _chosen);
    }
}
