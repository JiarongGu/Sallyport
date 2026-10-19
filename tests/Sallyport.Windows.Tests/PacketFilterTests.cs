using System.Security.Cryptography;
using Sallyport.Windows.PacketFilter;

namespace Sallyport.Windows.Tests;

public sealed class PacketFilterTests
{
    [Fact]
    public void The_version_control_code_is_the_drivers()
    {
        // CTL_CODE(FILE_DEVICE_NDISRD 0x8300, NDISRD_IOCTL_INDEX 0x830, METHOD_BUFFERED, FILE_ANY_ACCESS).
        Assert.Equal(0x830020C0u, Native.IoctlGetVersion);
        Assert.Equal(0x830020C4u, Native.ControlCode(0x8300, 0x831, 0, 0));
    }

    [Theory]
    [InlineData(0, true, false, false)]
    [InlineData(3010, true, true, false)]
    [InlineData(1641, true, true, false)]
    [InlineData(1602, false, false, true)]
    [InlineData(1223, false, false, true)]
    [InlineData(1618, false, false, false)]
    [InlineData(1603, false, false, false)]
    public void Windows_Installer_exit_codes_mean_what_they_say(int code, bool succeeded, bool reboot, bool cancelled)
    {
        var outcome = PacketFilterSetup.Interpret(code, "install", null);
        Assert.Equal((succeeded, reboot, cancelled), (outcome.Succeeded, outcome.RebootNeeded, outcome.Cancelled));
    }

    [Fact]
    public async Task Only_the_pinned_bytes_match()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, "not the installer"u8.ToArray());
            var actual = Convert.ToHexString(SHA256.HashData("not the installer"u8));
            Assert.False(await PacketFilterSetup.MatchesAsync(file, default));
            Assert.True(await PacketFilterSetup.MatchesAsync(file, default, actual));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void The_pinned_release_is_NT_Kernels_own()
    {
        Assert.Equal("github.com", PacketFilterRelease.Installer.Host);
        Assert.StartsWith("/wiresock/ndisapi/releases/download/", PacketFilterRelease.Installer.AbsolutePath);
        Assert.Equal(64, PacketFilterRelease.InstallerSha256.Length);
        Assert.EndsWith(PacketFilterRelease.InstallerFileName, PacketFilterRelease.Installer.AbsolutePath);
    }

    [Fact]
    public void A_status_says_which_part_answered()
    {
        Assert.Equal("0x06023000", new PacketFilterStatus(ServiceState.Running, 0x06023000, null, null).VersionText);
        Assert.False(new PacketFilterStatus(ServiceState.Running, null, 5, null).Usable);
    }
}
