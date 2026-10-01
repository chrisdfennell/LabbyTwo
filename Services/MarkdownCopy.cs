using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LabbyTwo.Services;

/// <summary>
/// A small "copy" button beside every code block and code span in a note.
///
/// A runbook is mostly commands somebody is going to paste into a terminal at two in the
/// morning, and selecting <c>Get-Service NTDS,Netlogon,Kdc,DNS</c> out of a numbered list
/// on a phone is exactly the fiddly thing that gets a character missed. One tap copies the
/// code's own text, nothing around it.
///
/// Written on Markdig's syntax tree, like the callouts, rather than by editing its HTML:
/// the button is fixed markup this class writes, the code is still escaped by Markdig's own
/// renderer, and raw HTML stays off — nothing a note says is ever written out as a tag. The
/// button carries no handler and no data; one delegated listener in <c>markdown.js</c> finds
/// the code next to whichever button was pressed, so the same HTML works in a plain note,
/// one with live values in it (where the markup is read back into elements, see
/// <see cref="MarkupTree"/>), and before the circuit has started.
///
/// The wrappers are a <c>span</c> for a code span, so a sentence, a list item or a table
/// cell holding one is still exactly the sentence, list item or cell it was, and a
/// <c>div</c> around a block, which is already a block of its own.
/// </summary>
public sealed class CopyButtonExtension : IMarkdownExtension
{
    /// <summary>The button, exactly as written. Its label and icon are drawn by CSS, so copying the note's text never picks up the word "Copy".</summary>
    public const string Button = "<button type=\"button\" class=\"md-copy\" aria-label=\"Copy\" title=\"Copy\"></button>";

    public void Setup(MarkdownPipelineBuilder pipeline)
    {
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is not HtmlRenderer html)
            return;

        // Wrapping the renderers already there rather than replacing them keeps whatever the
        // other extensions told them — the diagram extension's list of blocks drawn as a
        // mermaid picture rather than as code, for one.
        if (html.ObjectRenderers.FindExact<CodeBlockRenderer>() is { } blocks)
            html.ObjectRenderers.Replace<CodeBlockRenderer>(new BlockRenderer(blocks));
        if (html.ObjectRenderers.FindExact<CodeInlineRenderer>() is { } spans)
            html.ObjectRenderers.Replace<CodeInlineRenderer>(new SpanRenderer(spans));
    }

    private sealed class BlockRenderer(CodeBlockRenderer inner) : HtmlObjectRenderer<CodeBlock>
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock block)
        {
            // A mermaid diagram is not code anybody copies, and it is drawn as a picture.
            var diagram = block is FencedCodeBlock { Info: { Length: > 0 } info }
                          && (inner.BlocksAsDiv.Contains(info) || inner.BlockMapping.ContainsKey(info));
            if (diagram || !renderer.EnableHtmlForBlock)
            {
                ((IMarkdownObjectRenderer)inner).Write(renderer, block);
                return;
            }

            renderer.EnsureLine();
            renderer.Write("<div class=\"md-code\">");
            ((IMarkdownObjectRenderer)inner).Write(renderer, block);
            renderer.EnsureLine();
            renderer.Write(Button).WriteLine("</div>");
        }
    }

    private sealed class SpanRenderer(CodeInlineRenderer inner) : HtmlObjectRenderer<CodeInline>
    {
        protected override void Write(HtmlRenderer renderer, CodeInline code)
        {
            // Not inside a link, where a button would be a control inside a control and a
            // tap meant for the link would copy instead; and not where HTML is off for
            // inlines, which is an image's alt text.
            if (!renderer.EnableHtmlForInline || InsideLink(code))
            {
                ((IMarkdownObjectRenderer)inner).Write(renderer, code);
                return;
            }

            renderer.Write("<span class=\"md-code-inline\">");
            ((IMarkdownObjectRenderer)inner).Write(renderer, code);
            renderer.Write(Button).Write("</span>");
        }

        private static bool InsideLink(Inline inline)
        {
            for (var parent = inline.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent is LinkInline)
                    return true;
            }
            return false;
        }
    }
}

public static class CopyButtonPipeline
{
    /// <summary>A copy button beside each code block and code span. See <see cref="CopyButtonExtension"/>.</summary>
    public static MarkdownPipelineBuilder UseCopyButtons(this MarkdownPipelineBuilder builder)
    {
        builder.Extensions.AddIfNotAlready<CopyButtonExtension>();
        return builder;
    }
}
