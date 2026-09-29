using LabbyTwo.Services;

namespace LabbyTwo.Tests;

/// <summary>
/// Finding this container's id from /proc rather than the hostname. The hostname is what
/// Watchtower carries over from the container it replaced, so after an update it names a
/// container that no longer exists — and "Update now" disappeared until the container was
/// recreated by hand.
/// </summary>
public sealed class SelfContainerTests
{
    private const string Id = "4f1c2b9e8d7a6f5e4d3c2b1a09f8e7d6c5b4a3928170f6e5d4c3b2a190817263";
    private const string Layer = "a9b8c7d6e5f4a3b2c1d0e9f8a7b6c5d4e3f2a1b0c9d8e7f6a5b4c3d2e1f0a9b8";

    [Fact]
    public void Reads_the_id_from_the_files_docker_mounts_into_the_container()
    {
        // A trimmed /proc/self/mountinfo from a container on a QNAP: the root filesystem is an
        // overlay whose options name image layers, and /etc/hostname and friends are bind
        // mounts from the container's own directory.
        var mountinfo = $"""
            1422 1203 0:190 / / rw,relatime - overlay overlay rw,lowerdir=/share/CACHEDEV1_DATA/Container/container-station-data/lib/docker/overlay2/l/ABC:/share/CACHEDEV1_DATA/Container/container-station-data/lib/docker/overlay2/{Layer}/diff
            1423 1422 0:193 / /proc rw,nosuid,nodev,noexec,relatime - proc proc rw
            1431 1422 253:0 /Container/container-station-data/lib/docker/containers/{Id}/resolv.conf /etc/resolv.conf rw,relatime - ext4 /dev/mapper/cachedev1 rw
            1432 1422 253:0 /Container/container-station-data/lib/docker/containers/{Id}/hostname /etc/hostname rw,relatime - ext4 /dev/mapper/cachedev1 rw
            1433 1422 0:26 /docker.sock /var/run/docker.sock rw,nosuid,nodev - tmpfs tmpfs rw
            """;

        Assert.Equal(Id, SelfContainer.IdFrom(mountinfo));
    }

    [Theory]
    [InlineData($"12:memory:/docker/{Id}")]
    [InlineData($"0::/system.slice/docker-{Id}.scope")]
    public void Reads_the_id_from_a_cgroup_listing(string cgroup) =>
        Assert.Equal(Id, SelfContainer.IdFrom(cgroup));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0::/")]
    [InlineData($"1 0 0:1 / / rw - overlay overlay rw,lowerdir=/var/lib/docker/overlay2/{Layer}/diff")]
    public void Finds_nothing_where_no_container_is_named(string? listing) =>
        Assert.Null(SelfContainer.IdFrom(listing));
}
