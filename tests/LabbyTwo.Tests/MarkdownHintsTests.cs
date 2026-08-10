using LabbyTwo.Core;

namespace LabbyTwo.Tests;

public class MarkdownHintsTests
{
    [Theory]
    [InlineData("##Wi-Fi Password:", "## Wi-Fi Password:")]
    [InlineData("#Router", "# Router")]
    [InlineData("######Deep", "###### Deep")]
    [InlineData("   ##Indented three spaces still counts", "   ## Indented three spaces still counts")]
    public void Flags_a_heading_written_without_its_space(string line, string expected)
    {
        var hint = Assert.Single(MarkdownHints.For(line));

        Assert.Equal(1, hint.Line);
        Assert.Equal(line, hint.Text);
        Assert.Equal(expected, hint.Suggestion);
        Assert.Equal(expected, MarkdownHints.Fix(line));
    }

    [Theory]
    [InlineData("## Wi-Fi Password:")]      // already correct
    [InlineData("#1 priority")]             // a number, not a heading
    [InlineData("#!/bin/sh")]               // a shebang
    [InlineData("#-- divider --")]          // punctuation
    [InlineData("#include <stdio.h>")]      // code that would be mangled by "fixing" it
    [InlineData("#define MAX 10")]
    [InlineData("    #Indented four spaces is a code block")]
    [InlineData("Text with #hash in the middle")]
    [InlineData("#######Seven hashes is not a heading at all")]
    [InlineData("")]
    public void Leaves_everything_else_alone(string line)
    {
        Assert.Empty(MarkdownHints.For(line));
        Assert.Equal(line, MarkdownHints.Fix(line));
    }

    [Fact]
    public void Ignores_fenced_code_where_a_hash_is_a_comment()
    {
        const string source = """
            # Real heading

            ```sh
            #Not a heading, a shell comment
            ```

            ##Broken
            """;

        var hint = Assert.Single(MarkdownHints.For(source));

        Assert.Equal(7, hint.Line);
        Assert.Equal("##Broken", hint.Text);
        Assert.Contains("#Not a heading", MarkdownHints.Fix(source));
        Assert.Contains("## Broken", MarkdownHints.Fix(source));
    }

    [Fact]
    public void A_tilde_fence_does_not_close_a_backtick_one()
    {
        const string source = """
            ```
            ~~~
            #Still inside the backtick fence
            ```
            ##Outside
            """;

        var hint = Assert.Single(MarkdownHints.For(source));
        Assert.Equal("##Outside", hint.Text);
    }

    [Fact]
    public void Reports_every_offending_line_with_its_number()
    {
        const string source = "##One\n\nsome text\n\n###Two";

        var hints = MarkdownHints.For(source);

        Assert.Equal([1, 5], hints.Select(h => h.Line));
        Assert.Equal("## One\n\nsome text\n\n### Two", MarkdownHints.Fix(source));
    }

    [Fact]
    public void Fixing_preserves_windows_line_endings()
    {
        // Repairing one heading must not rewrite the whole note's line endings.
        Assert.Equal("## A\r\ntext\r\n## B\r\n", MarkdownHints.Fix("##A\r\ntext\r\n##B\r\n"));
    }

    [Fact]
    public void Fixing_is_idempotent_and_clears_the_hints()
    {
        const string source = "##A\n#B\n";

        var once = MarkdownHints.Fix(source);

        Assert.Empty(MarkdownHints.For(once));
        Assert.Equal(once, MarkdownHints.Fix(once));
    }

    [Fact]
    public void Handles_no_content()
    {
        Assert.Empty(MarkdownHints.For(null));
        Assert.Equal("", MarkdownHints.Fix(null));
    }
}
