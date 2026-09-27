using System.Net;
using System.Text;
using LabbyTwo.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// Bookmark icons: which links in a page's head count as one, and what the cache does with
/// a request that was abandoned rather than answered.
/// </summary>
public class FaviconServiceTests
{
    [Fact]
    public void TheOrdinaryDeclarationsAreAllFound()
    {
        var hrefs = FaviconService.IconHrefs("""
            <head>
              <link rel="icon" href="/a.png">
              <link rel="shortcut icon" href="/b.ico">
              <LINK REL='apple-touch-icon' HREF='/c.png' />
              <link href="/d.svg" type="image/svg+xml" rel="icon">
              <link rel=icon href=/e.png>
            </head>
            """);

        Assert.Equal(["/a.png", "/b.ico", "/c.png", "/d.svg", "/e.png"], hrefs);
    }

    [Fact]
    public void RelsThatMerelyContainTheWordAreNotIcons()
    {
        // GitHub declares both. The fluid icon is a 512px app tile and the mask icon a
        // one-colour silhouette meant to be tinted — neither is what a bookmark wants, and
        // either one found first would be what it got.
        var hrefs = FaviconService.IconHrefs("""
            <link rel="fluid-icon" href="/fluid.png">
            <link rel="mask-icon" href="/pinned.svg" color="#000">
            <link rel="icon" href="/real.svg">
            """);

        Assert.Equal(["/real.svg"], hrefs);
    }

    [Fact]
    public void AnHrefInsideAnotherAttributesNameIsNotTheHref()
    {
        var hrefs = FaviconService.IconHrefs(
            """<link rel="icon" data-base-href="/wrong/" href="/right.png">""");

        Assert.Equal(["/right.png"], hrefs);
    }

    [Fact]
    public void EntitiesInTheHrefAreDecoded()
    {
        var hrefs = FaviconService.IconHrefs("""<link rel="icon" href="/icon?v=2&amp;s=32">""");

        Assert.Equal(["/icon?v=2&s=32"], hrefs);
    }

    [Fact]
    public void LinksThatAreNotIconsAreIgnored()
    {
        var hrefs = FaviconService.IconHrefs("""
            <link rel="stylesheet" href="/site.css">
            <link rel="preload" as="image" href="/icon.png">
            <link rel="icon">
            """);

        Assert.Empty(hrefs);
    }

    [Fact]
    public void ThePlaceholderIsAnImage()
    {
        // It is served in place of a 404, so it has to be something an <img> will draw.
        Assert.True(FaviconService.Icon.Placeholder.Found);
        Assert.Equal("image/svg+xml", FaviconService.Icon.Placeholder.ContentType);
        Assert.StartsWith("<svg", Encoding.UTF8.GetString(FaviconService.Icon.Placeholder.Bytes));
    }

    [Fact]
    public async Task AnAbandonedRequestIsNotRememberedAsNoIcon()
    {
        var handler = new Handler();
        var icons = new FaviconService(new Factory(handler), NullLogger<FaviconService>.Instance);

        // The first request is cancelled while the site is still thinking about it — the
        // browser navigated away. That says nothing about the site.
        handler.Hang = true;
        using (var abandoned = new CancellationTokenSource())
        {
            var pending = icons.GetAsync("http://nas.lan/", abandoned.Token);
            await handler.Started.Task;
            abandoned.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        // So the next page load asks again and gets the icon, rather than an hour of
        // nothing.
        handler.Hang = false;
        var icon = await icons.GetAsync("http://nas.lan/");

        Assert.True(icon.Found);
        Assert.Equal("image/png", icon.ContentType);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A site with a favicon.ico and no HTML, that can be told to never answer.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        public bool Hang { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Hang)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            if (request.RequestUri!.AbsolutePath == "/favicon.ico")
            {
                var content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]);
                content.Headers.ContentType = new("image/png");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
