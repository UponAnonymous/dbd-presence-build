// Covers: FR-PACK-10, ADR-0013 and ADR-0016 (the image helper never opens network paths).
using DbdPresence.Core.Images;

namespace DbdPresence.Core.Tests.Images;

public sealed class ImagePathPolicyTests
{
    [Theory]
    [InlineData(@"\\server\share\a.png")]
    [InlineData(@"\\?\UNC\server\share\a.png")]
    [InlineData(@"\\?\C:\images\a.png")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData("//server/share/a.png")]
    public void UncAndDevicePaths_AreRejected_WithoutAskingForADriveType(string path)
    {
        var asked = false;

        var allowed = ImagePathPolicy.IsLocalPath(path, _ =>
        {
            asked = true;
            return DriveType.Fixed;
        });

        Assert.False(allowed);
        Assert.False(asked);
    }

    [Theory]
    [InlineData(DriveType.Fixed, true)]
    [InlineData(DriveType.Removable, true)]
    [InlineData(DriveType.Network, false)]
    [InlineData(DriveType.CDRom, false)]
    [InlineData(DriveType.Ram, false)]
    [InlineData(DriveType.Unknown, false)]
    [InlineData(DriveType.NoRootDirectory, false)]
    public void OnlyFixedAndRemovableDrivesAreAllowed(DriveType type, bool expected)
    {
        Assert.Equal(expected, ImagePathPolicy.IsLocalPath(@"C:\images\a.png", _ => type));
    }

    [Fact]
    public void ADriveTheOsRejects_IsNotLocal()
    {
        Assert.False(ImagePathPolicy.IsLocalPath(@"C:\a.png", _ => throw new ArgumentException("no such drive")));
    }

    [Fact]
    public void TheDriveRootIsPassedToTheLookup()
    {
        string? seen = null;

        Assert.True(ImagePathPolicy.IsLocalPath(@"D:\art\a.png", root =>
        {
            seen = root;
            return DriveType.Removable;
        }));
        Assert.Equal(@"D:\", seen);
    }

    [Fact]
    public void ARelativePath_HasNoRootAndIsRejected()
    {
        Assert.False(ImagePathPolicy.IsLocalPath("a.png", _ => DriveType.Fixed));
    }
}
