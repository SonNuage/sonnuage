using SonNuage.Server.Api;

namespace SonNuage.Server.Tests;

/// <summary>Dans un conteneur, /music et /uploads sont deux points de montage d'un même disque : la bibliothèque y compte.</summary>
public sealed class DashboardStorageTests
{
    private const long Gb = 1L << 30;

    [Fact]
    public void Two_mounts_of_one_disk_are_the_same_disk()
    {
        var uploads = new DashboardEndpoints.Disk("/uploads", "ext4", 457 * Gb, 318 * Gb);
        var music = new DashboardEndpoints.Disk("/music", "ext4", 457 * Gb, 318 * Gb + 200 * 1024 * 1024); // copie en cours entre deux lectures
        Assert.True(DashboardEndpoints.SameDisk(uploads, music));
    }

    [Theory]
    [InlineData("ext4", 500L, 318L)] // autre taille
    [InlineData("xfs", 457L, 318L)] // autre système de fichiers
    [InlineData("ext4", 457L, 100L)] // même taille, mais pas le même espace libre
    public void Another_disk_is_not_the_same_disk(string format, long totalGb, long freeGb)
    {
        var uploads = new DashboardEndpoints.Disk("/uploads", "ext4", 457 * Gb, 318 * Gb);
        Assert.False(DashboardEndpoints.SameDisk(uploads, new DashboardEndpoints.Disk("/mnt/other", format, totalGb * Gb, freeGb * Gb)));
    }
}
