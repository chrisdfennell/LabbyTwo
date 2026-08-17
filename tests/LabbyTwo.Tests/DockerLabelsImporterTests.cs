using System.Text;
using LabbyTwo.Core;
using LabbyTwo.DockerLabelsPlugin;

namespace LabbyTwo.Tests;

/// <summary>
/// The Docker label importer, which is a pure function of a file and therefore the easiest
/// kind of extension to be sure about. What is worth pinning here is not the happy path so
/// much as the ways a real container list is awkward: labels in two different places, names
/// with a leading slash, two containers claiming the same name, and the very common case of
/// somebody labelling a container and forgetting the one label that matters.
/// </summary>
public sealed class DockerLabelsImporterTests
{
    private static ImportSource Source(string json, string name = "containers.json") =>
        new(name, Encoding.UTF8.GetBytes(json));

    private const string TwoContainers =
        """
        [
          {
            "Id": "abc123def456789",
            "Names": ["/jellyfin"],
            "Image": "jellyfin/jellyfin",
            "State": "running",
            "Labels": {
              "labbytwo.enable": "true",
              "labbytwo.name": "Jellyfin",
              "labbytwo.url": "http://192.168.86.57:8096",
              "labbytwo.icon": "🎬",
              "labbytwo.tab": "Media"
            }
          },
          {
            "Id": "def456abc123789",
            "Names": ["/paperless"],
            "Image": "paperless-ngx/paperless",
            "State": "running",
            "Labels": {
              "labbytwo.enable": "true",
              "labbytwo.url": "http://192.168.86.57:8000",
              "labbytwo.provider": "paperless"
            }
          }
        ]
        """;

    [Fact]
    public void ALabelledContainerBecomesAConnectionAndATile()
    {
        var plan = new DockerLabelsImporter().Read(Source(TwoContainers));

        var jellyfin = Assert.Single(plan.Connections, c => c.Name == "Jellyfin");
        Assert.Equal("http", jellyfin.Provider);
        Assert.Equal("http://192.168.86.57:8096", jellyfin.Values.Get("url"));
        Assert.Equal("🎬", jellyfin.Icon);

        var media = Assert.Single(plan.Tabs, t => t.Name == "Media");
        var tile = Assert.Single(media.Widgets);
        Assert.Equal("service-tile", tile.Type);
        Assert.Equal(jellyfin.Ref, tile.ConnectionRef);
    }

    /// <summary>
    /// The point of the provider label. A container running something LabbyTwo has a real
    /// integration for should get that integration, not a URL that is only checked for a
    /// response.
    /// </summary>
    [Fact]
    public void TheProviderLabelChoosesTheIntegration()
    {
        var plan = new DockerLabelsImporter().Read(Source(TwoContainers));

        var paperless = Assert.Single(plan.Connections, c => c.Provider == "paperless");

        // No name label, so the container's own name is used rather than a blank.
        Assert.Equal("paperless", paperless.Name);
    }

    [Fact]
    public void ContainersWithoutTheEnableLabelAreLeftAlone()
    {
        var json =
            """
            [
              { "Id": "aaa", "Names": ["/redis"], "Image": "redis", "Labels": {} },
              { "Id": "bbb", "Names": ["/db"], "Image": "postgres", "Labels": { "labbytwo.url": "http://x" } }
            ]
            """;

        var plan = new DockerLabelsImporter().Read(Source(json));

        Assert.Empty(plan.Connections);
        Assert.Empty(plan.Tabs);
        Assert.Contains(plan.Notes, note => note.Contains("labbytwo.enable=true"));
    }

    /// <summary>
    /// The mistake everybody makes once. Being told which label is missing — and what the
    /// value probably is — is the difference between fixing it and giving up on the feature.
    /// </summary>
    [Fact]
    public void AContainerMissingItsUrlIsNamedAndItsPortSuggested()
    {
        var json =
            """
            [{
              "Id": "ccc", "Names": ["/grafana"], "Image": "grafana/grafana",
              "Ports": [{ "PrivatePort": 3000, "PublicPort": 3001, "Type": "tcp" }],
              "Labels": { "labbytwo.enable": "true" }
            }]
            """;

        var plan = new DockerLabelsImporter().Read(Source(json));

        Assert.Empty(plan.Connections);
        var note = Assert.Single(plan.Notes, n => n.Contains("grafana"));
        Assert.Contains("labbytwo.url", note);
        Assert.Contains("3001", note);
    }

    /// <summary>
    /// <c>docker inspect</c> nests labels under Config and uses a singular Name. People
    /// paste whichever command they happened to run.
    /// </summary>
    [Fact]
    public void TheInspectShapeIsReadToo()
    {
        var json =
            """
            [{
              "Id": "ddd",
              "Name": "/immich",
              "Config": {
                "Image": "immich",
                "Labels": { "labbytwo.enable": "true", "labbytwo.url": "http://192.168.86.57:2283" }
              }
            }]
            """;

        var plan = new DockerLabelsImporter().Read(Source(json));

        var connection = Assert.Single(plan.Connections);
        Assert.Equal("immich", connection.Name);
        Assert.Equal("http://192.168.86.57:2283", connection.Values.Get("url"));
    }

    [Fact]
    public void TwoContainersClaimingOneNameDoNotBothArrive()
    {
        var json =
            """
            [
              { "Id": "e1", "Names": ["/a"], "Image": "x",
                "Labels": { "labbytwo.enable": "true", "labbytwo.name": "App", "labbytwo.url": "http://one" } },
              { "Id": "e2", "Names": ["/b"], "Image": "x",
                "Labels": { "labbytwo.enable": "true", "labbytwo.name": "App", "labbytwo.url": "http://two" } }
            ]
            """;

        var plan = new DockerLabelsImporter().Read(Source(json));

        Assert.Single(plan.Connections);
        Assert.Contains(plan.Notes, note => note.Contains("More than one"));
    }

    [Fact]
    public void ContainersOnTheSameTabShareIt()
    {
        var json =
            """
            [
              { "Id": "f1", "Names": ["/one"], "Image": "x",
                "Labels": { "labbytwo.enable": "true", "labbytwo.url": "http://one", "labbytwo.tab": "Media" } },
              { "Id": "f2", "Names": ["/two"], "Image": "x",
                "Labels": { "labbytwo.enable": "true", "labbytwo.url": "http://two", "labbytwo.tab": "Media" } }
            ]
            """;

        var plan = new DockerLabelsImporter().Read(Source(json));

        var tab = Assert.Single(plan.Tabs);
        Assert.Equal("Media", tab.Name);
        Assert.Equal(2, tab.Widgets.Count);
    }

    [Fact]
    public void DetectionAcceptsDockerOutputAndRefusesEverythingElse()
    {
        var importer = new DockerLabelsImporter();

        Assert.True(importer.CanHandle(Source(TwoContainers)));
        Assert.False(importer.CanHandle(Source("""{"services": {}}""")));
        Assert.False(importer.CanHandle(Source("not json at all")));

        // Another dashboard's JSON export is an array of the wrong things.
        Assert.False(importer.CanHandle(Source("""[{"name":"Plex","url":"http://x"}]""")));
    }

    /// <summary>A detector must not throw, or the importers after it never get asked.</summary>
    [Fact]
    public void DetectionSurvivesRubbish()
    {
        var importer = new DockerLabelsImporter();

        Assert.False(importer.CanHandle(Source("[{\"Id\":", "broken.json")));
        Assert.False(importer.CanHandle(Source("", "empty.json")));
        Assert.False(importer.CanHandle(Source(TwoContainers, "containers.yml")));
    }

    [Fact]
    public void SomethingThatIsNotAnArrayIsRefusedWithAReadableMessage()
    {
        var error = Assert.Throws<FormatException>(
            () => new DockerLabelsImporter().Read(Source("""{"Id":"abc"}""")));

        Assert.Contains("array", error.Message);
    }
}
