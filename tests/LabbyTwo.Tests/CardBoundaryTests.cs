using LabbyTwo.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// The boundary every card sits in. Rendered with the framework's own HTML renderer rather
/// than a component-testing library, which is enough to show the one thing that matters: a
/// widget that throws leaves a notice behind instead of taking the render with it.
/// </summary>
public class CardBoundaryTests
{
    private static async Task<string> RenderAsync(Type child, string? name = null)
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            // The framework's boundary asks for this even though CardBoundary logs for itself.
            .AddSingleton<IErrorBoundaryLogger, QuietBoundaryLogger>()
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CardBoundary.Name)] = name,
                [nameof(CardBoundary.ChildContent)] = (RenderFragment)(builder =>
                {
                    builder.OpenComponent(0, child);
                    builder.CloseComponent();
                }),
            });
            var output = await renderer.RenderComponentAsync<CardBoundary>(parameters);
            return output.ToHtmlString();
        });
    }

    [Fact]
    public async Task AWidgetThatThrowsBecomesANotice()
    {
        var html = await RenderAsync(typeof(Throws), "Plex");

        Assert.Contains("card-failed", html);
        Assert.Contains("Plex failed to load.", html);
    }

    [Fact]
    public async Task AWidgetThatWorksIsLeftAlone()
    {
        var html = await RenderAsync(typeof(Works));

        Assert.Contains("fine", html);
        Assert.DoesNotContain("card-failed", html);
    }

    private sealed class QuietBoundaryLogger : IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
    }

    private sealed class Throws : ComponentBase
    {
        protected override void OnInitialized() => throw new InvalidOperationException("the provider answered in Klingon");
    }

    private sealed class Works : ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) =>
            builder.AddContent(0, "fine");
    }
}
