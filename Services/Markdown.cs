using Markdig;

namespace LabbyTwo.Services;

/// <summary>
/// Renders user-written markdown. Note content is authored by whoever can already
/// reconfigure the whole app, so this is a formatting convenience rather than a trust
/// boundary — but raw HTML stays disabled so a pasted snippet cannot quietly run script
/// in every other viewer's session.
/// </summary>
public sealed class Markdown
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public string ToHtml(string? markdown) =>
        string.IsNullOrWhiteSpace(markdown) ? "" : Markdig.Markdown.ToHtml(markdown, _pipeline);
}
