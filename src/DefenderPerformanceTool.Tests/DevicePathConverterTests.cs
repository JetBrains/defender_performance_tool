using System.Collections.Generic;
using Xunit;

namespace DefenderPerformanceTool.Tests;

public class DevicePathConverterTests
{
    private static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string>
        {
            [@"\Device\HarddiskVolume3"] = "C:",
            [@"\Device\HarddiskVolume5"] = "D:",
        };

    [Fact]
    public void ToDosPath_ConvertsKnownVolumeToDriveLetter()
    {
        var result = DevicePathConverter.ToDosPath(
            @"\Device\HarddiskVolume3\Windows\SystemApps\App.dll", Map);
        Assert.Equal(@"C:\Windows\SystemApps\App.dll", result);
    }

    [Fact]
    public void ToDosPath_MatchesCaseInsensitively()
    {
        var result = DevicePathConverter.ToDosPath(
            @"\DEVICE\HARDDISKVOLUME5\media\movie.mkv", Map);
        Assert.Equal(@"D:\media\movie.mkv", result);
    }

    [Fact]
    public void ToDosPath_LeavesDosPathsUnchanged()
    {
        Assert.Equal(@"C:\work\a.cs", DevicePathConverter.ToDosPath(@"C:\work\a.cs", Map));
    }

    [Fact]
    public void ToDosPath_LeavesUnknownVolumesUnchanged()
    {
        const string path = @"\Device\LanmanRedirector\server\share\file.txt";
        Assert.Equal(path, DevicePathConverter.ToDosPath(path, Map));
    }

    [Fact]
    public void ToDosPath_DoesNotMatchPartialVolumeNames()
    {
        // "HarddiskVolume3x" must not match the "HarddiskVolume3" entry.
        const string path = @"\Device\HarddiskVolume30\file.txt";
        Assert.Equal(path, DevicePathConverter.ToDosPath(path, Map));
    }
}
