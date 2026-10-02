#pragma warning disable BL0006 // The interactive test renderer reads its own render tree to find what to click and change.
using System.Net;
using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using LabbyTwo.Providers;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// The step builder and the secrets list, drawn by the real components: steps added,
/// reordered and removed land in the stored JSON, an example fills the list in, a
/// reference to nothing is pointed out while it is being typed, "Run now" shows each
/// step's answer with the secrets masked, and a saved secret is never put into the page.
/// </summary>
public sealed class MultiStepCheckEditorTests : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly InteractiveRenderer _renderer;
    private readonly SettingsBag _settings = new();
    private string? _value;

    public MultiStepCheckEditorTests()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddSingleton<IEnumerable<IConnectionProvider>>(
            [new MultiStepCheckProvider(NullLogger<ProviderHttpLogger>.Instance)]);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        _services = services.BuildServiceProvider();
        _renderer = new InteractiveRenderer(_services);
    }

    public async ValueTask DisposeAsync()
    {
        await _renderer.DisposeAsync();
        await _services.DisposeAsync();
    }

    private Task RenderAsync(IEnumerable<CheckStep>? steps = null)
    {
        _value = steps is null ? null : StepCheck.Serialize(steps);
        if (_value is not null)
            _settings["steps"] = _value;
        return _renderer.RenderAsync<StepCheckEditor>(new Dictionary<string, object?>
        {
            [nameof(StepCheckEditor.Value)] = _value,
            [nameof(StepCheckEditor.Settings)] = _settings,
            [nameof(StepCheckEditor.ValueChanged)] = EventCallback.Factory.Create<string>(this, json =>
            {
                _value = json;
                _settings["steps"] = json;
            }),
        });
    }

    private List<CheckStep> Stored => StepCheck.Parse(_value);

    private async Task<string> HtmlAsync() => WebUtility.HtmlDecode(await _renderer.HtmlAsync());

    [Fact]
    public async Task Steps_are_added_named_reordered_and_removed()
    {
        await RenderAsync();
        Assert.Contains("No steps yet", await HtmlAsync());

        await _renderer.ClickAsync("+ Add a step");
        await _renderer.ChangeAsync("step-0-name", "Log in");
        await _renderer.ChangeAsync("step-0-url", "http://app.lan/login");
        await _renderer.ClickAsync("+ Add a step");
        await _renderer.ChangeAsync("step-1-name", "Dashboard");

        Assert.Equal(["Log in", "Dashboard"], Stored.Select(s => s.Name));
        // A new step starts at the same app as the one before, with a status check to edit.
        Assert.Equal("http://app.lan/", Stored[1].Url);
        Assert.Equal(StepAssertKind.Status, Stored[1].Checks.Single().Kind);

        await _renderer.ClickAsync("Move down");
        Assert.Equal(["Dashboard", "Log in"], Stored.Select(s => s.Name));

        await _renderer.ClickAsync("Remove step");
        Assert.Equal(["Log in"], Stored.Select(s => s.Name));
    }

    [Fact]
    public async Task Checks_and_saved_values_are_built_row_by_row()
    {
        await RenderAsync([new CheckStep { Name = "Token", Url = "http://api.lan/token" }]);

        await _renderer.ClickAsync("+ Add a check");
        await _renderer.ClickAsync("+ Add a check");
        await _renderer.ChangeAsync("step-0-check-1-kind", nameof(StepAssertKind.JsonEquals));
        await _renderer.ChangeAsync("step-0-check-1-target", "status");
        await _renderer.ChangeAsync("step-0-check-1-value", "ok");
        await _renderer.ClickAsync("+ Save a value");
        await _renderer.ChangeAsync("step-0-save-0-name", "token");
        await _renderer.ChangeAsync("step-0-save-0-expression", "access_token");

        var step = Stored.Single();
        Assert.Equal(StepAssertKind.Status, step.Checks[0].Kind);
        Assert.Equal("200", step.Checks[0].Value);
        Assert.Equal(StepAssertKind.JsonEquals, step.Checks[1].Kind);
        Assert.Equal(("status", "ok"), (step.Checks[1].Target, step.Checks[1].Value));
        Assert.Equal(("token", StepExtractFrom.Json, "access_token", true),
            (step.Save[0].Name, step.Save[0].From, step.Save[0].Expression, step.Save[0].Hide));
        Assert.Contains("Later steps use these as ${token}", await HtmlAsync());

        await _renderer.ClickAsync("Remove check");
        Assert.Equal(StepAssertKind.JsonEquals, Stored.Single().Checks.Single().Kind);
    }

    [Fact]
    public async Task An_example_fills_the_steps_in_and_says_which_secrets_it_expects()
    {
        await RenderAsync();
        await _renderer.ChangeAsync("step-template", "api-token");
        var html = await HtmlAsync();
        Assert.Contains("Secrets it expects: password.", html);

        await _renderer.ClickAsync("Use this example");

        Assert.Equal(["Get a token", "Check the status"], Stored.Select(s => s.Name));
        Assert.Contains("Replace the steps with this example", await HtmlAsync());
    }

    [Fact]
    public async Task A_reference_nothing_sets_is_pointed_out_while_it_is_written()
    {
        await RenderAsync(
        [
            new CheckStep { Name = "One", Url = "http://a.lan/", Save = [new() { Name = "csrf", Expression = "x" }] },
            new CheckStep { Name = "Two", Url = "http://a.lan/${csrf}/${token}", Headers = "X-Key: ${secret:key}" },
        ]);

        var html = await HtmlAsync();
        Assert.Contains("${token} is not saved by any earlier step.", html);
        Assert.DoesNotContain("${csrf} is not saved", html);
        Assert.Contains("${secret:key} needs a secret called “key”", html);
    }

    [Fact]
    public async Task Run_now_shows_each_answer_with_the_secrets_masked()
    {
        using var app = new FakeApp(async (context, body) =>
        {
            context.Response.AppendHeader("X-Served-By", "fake");
            await FakeApp.WriteAsync(context, $"<h1>Hello</h1> you sent {body}");
        });
        _settings["secrets"] = StepCheck.SerializeSecrets(new Dictionary<string, string> { ["password"] = "correct-horse-battery" });
        await RenderAsync(
        [
            new CheckStep
            {
                Name = "Log in", Method = "POST", Url = $"{app.Base}/login", BodyKind = StepBodyKind.Form,
                Body = "password=${secret:password}", Headers = "Authorization: Bearer ${secret:password}",
                Checks = [new() { Kind = StepAssertKind.BodyContains, Value = "Hello" }],
            },
            new CheckStep
            {
                Name = "Missing", Url = $"{app.Base}/x",
                Checks = [new() { Kind = StepAssertKind.BodyContains, Value = "report.pdf" }],
            },
        ]);

        await _renderer.ClickAsync("Run now");
        var html = WebUtility.HtmlDecode(await _renderer.WaitForAsync(h => h.Contains("data-run", StringComparison.Ordinal)));

        Assert.Contains("Step 1 · Log in — HTTP 200 in", html);
        Assert.Contains("X-Served-By: fake", html);
        Assert.Contains("you sent password=••••", html);
        Assert.Contains("Authorization: ••••", html);
        Assert.Contains("Step 2 'Missing': expected the body to contain 'report.pdf' — it didn't", html);
        Assert.DoesNotContain("correct-horse-battery", html);
    }

    [Fact]
    public async Task A_saved_secret_shows_its_name_and_never_its_value()
    {
        string? value = StepCheck.SerializeSecrets(new Dictionary<string, string> { ["password"] = "correct-horse-battery" });
        await _renderer.RenderAsync<SecretListEditor>(new Dictionary<string, object?>
        {
            [nameof(SecretListEditor.Value)] = value,
            [nameof(SecretListEditor.ValueChanged)] = EventCallback.Factory.Create<string>(this, json => value = json),
        });

        var html = await HtmlAsync();
        Assert.Contains("value=\"password\"", html);
        Assert.Contains("${secret:password}", html);
        Assert.Contains("saved — type to replace", html);
        Assert.DoesNotContain("correct-horse-battery", html);

        // Leaving the box empty keeps the secret; typing replaces it; a new row adds one.
        await _renderer.ChangeAsync("secret-0-value", "");
        Assert.Equal("correct-horse-battery", StepCheck.ParseSecrets(value)["password"]);
        await _renderer.ClickAsync("+ Add a secret");
        await _renderer.ChangeAsync("secret-1-name", "token");
        await _renderer.ChangeAsync("secret-1-value", "abc-123");
        var secrets = StepCheck.ParseSecrets(value);
        Assert.Equal("correct-horse-battery", secrets["password"]);
        Assert.Equal("abc-123", secrets["token"]);
        Assert.DoesNotContain("abc-123", await HtmlAsync());

        await _renderer.ClickAsync("Remove secret");
        Assert.Equal(["token"], StepCheck.ParseSecrets(value).Keys);
    }
}
