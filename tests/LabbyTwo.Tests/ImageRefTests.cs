using LabbyTwo.Core;

namespace LabbyTwo.Tests;

public class ImageRefTests
{
    [Theory]
    [InlineData("fennch/labbytwo:latest", "docker.io", "fennch/labbytwo", "latest")]
    [InlineData("fennch/labbytwo", "docker.io", "fennch/labbytwo", "latest")]
    [InlineData("fennch/labbytwo:1029bc0", "docker.io", "fennch/labbytwo", "1029bc0")]
    [InlineData("postgres:17", "docker.io", "postgres", "17")]
    [InlineData("ghcr.io/someone/app:v2", "ghcr.io", "someone/app", "v2")]
    [InlineData("lscr.io/linuxserver/sonarr", "lscr.io", "linuxserver/sonarr", "latest")]
    public void Pulls_a_reference_apart(string image, string registry, string repository, string tag)
    {
        var parsed = ImageRef.Parse(image);

        Assert.Equal(registry, parsed.Registry);
        Assert.Equal(repository, parsed.Repository);
        Assert.Equal(tag, parsed.Tag);
    }

    [Fact]
    public void A_registry_port_is_not_a_tag()
    {
        // The trap: splitting on ':' first turns "localhost:5000/app" into the repository
        // "localhost" at tag "5000/app", and the update check then asks about the wrong
        // thing entirely.
        var parsed = ImageRef.Parse("localhost:5000/labbytwo");

        Assert.Equal("localhost:5000", parsed.Registry);
        Assert.Equal("labbytwo", parsed.Repository);
        Assert.Equal("latest", parsed.Tag);
    }

    [Fact]
    public void A_user_name_is_not_a_registry() =>
        // "fennch" has no dot, no port and is not localhost, so it is a Docker Hub user.
        Assert.True(ImageRef.Parse("fennch/labbytwo:latest").IsDockerHub);

    [Fact]
    public void A_registry_host_is_not_docker_hub() =>
        Assert.False(ImageRef.Parse("ghcr.io/someone/app").IsDockerHub);

    [Fact]
    public void A_digest_reference_keeps_the_repository()
    {
        var parsed = ImageRef.Parse("fennch/labbytwo@sha256:62a5ed3af3aa95b06f29d99513cc2d842566f8c905012558a471c170f35551a9");

        Assert.Equal("fennch/labbytwo", parsed.Repository);
        Assert.Equal("latest", parsed.Tag);
    }

    [Theory]
    [InlineData("postgres", "library/postgres")]
    [InlineData("fennch/labbytwo", "fennch/labbytwo")]
    public void Official_images_live_under_library(string image, string expected) =>
        // Docker Hub's API needs the namespace even where the CLI does not.
        Assert.Equal(expected, ImageRef.Parse(image).HubRepository);

    [Fact]
    public void A_locally_built_compose_image_is_still_readable()
    {
        // What "docker compose build" names an image: no registry, no user, no tag. It has
        // to parse rather than throw, because that is the case where the button is hidden.
        var parsed = ImageRef.Parse("labbytwo-labbytwo");

        Assert.Equal("labbytwo-labbytwo", parsed.Repository);
        Assert.Equal("labbytwo-labbytwo:latest", parsed.ToString());
    }

    [Fact]
    public void Nothing_parses_to_something_harmless() =>
        Assert.Equal("", ImageRef.Parse(null).Repository);
}
