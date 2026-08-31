using System;
using DefenderPerformanceTool.Tests.UiAutomation;
using Xunit;

namespace DefenderPerformanceTool.Tests;

/// <summary>
/// Tests for <see cref="DefenderExclusionManager"/> (in-process PowerShell hosting of
/// Get/Add/Remove-MpPreference). The round-trip tests modify the machine's real Windows
/// Defender configuration and are skipped unless the test host runs elevated.
/// </summary>
[Trait("Category", "RequiresElevation")]
public class DefenderExclusionManagerTests
{
    [Fact]
    public void PropertyName_maps_every_kind_to_the_MpPreference_property()
    {
        Assert.Equal("ExclusionPath", DefenderExclusionManager.PropertyName(ExclusionKind.Path));
        Assert.Equal("ExclusionProcess", DefenderExclusionManager.PropertyName(ExclusionKind.Process));
        Assert.Equal("ExclusionExtension", DefenderExclusionManager.PropertyName(ExclusionKind.Extension));
        Assert.Equal("ExclusionIpAddress", DefenderExclusionManager.PropertyName(ExclusionKind.IpAddress));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DefenderExclusionManager.PropertyName((ExclusionKind)(-1)));
    }

    [Fact]
    public void ExclusionSnapshot_For_returns_the_list_matching_the_kind()
    {
        var snapshot = new ExclusionSnapshot
        {
            Paths = new[] { @"C:\x" },
            Processes = new[] { "a.exe" },
            Extensions = new[] { ".abc" },
            IpAddresses = new[] { "10.0.0.1" }
        };

        Assert.Equal(snapshot.Paths, snapshot.For(ExclusionKind.Path));
        Assert.Equal(snapshot.Processes, snapshot.For(ExclusionKind.Process));
        Assert.Equal(snapshot.Extensions, snapshot.For(ExclusionKind.Extension));
        Assert.Equal(snapshot.IpAddresses, snapshot.For(ExclusionKind.IpAddress));
        Assert.Equal(4, snapshot.TotalCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.For((ExclusionKind)(-1)));
    }

    [SkippableFact]
    public void GetExclusions_reads_all_four_lists_from_Defender()
    {
        RequireElevatedDefender();

        var snapshot = DefenderExclusionManager.GetExclusions();

        Assert.NotNull(snapshot.Paths);
        Assert.NotNull(snapshot.Processes);
        Assert.NotNull(snapshot.Extensions);
        Assert.NotNull(snapshot.IpAddresses);
        Assert.False(snapshot.HiddenFromLocalUsers,
            "An elevated process must be able to read the real exclusion lists.");
    }

    [SkippableFact]
    public void Path_exclusion_can_be_added_and_removed() =>
        RunRoundTrip(ExclusionKind.Path, $@"C:\WdtApiTest\{Guid.NewGuid():N}");

    [SkippableFact]
    public void Process_exclusion_can_be_added_and_removed() =>
        RunRoundTrip(ExclusionKind.Process, $"wdt-api-test-{Guid.NewGuid():N}.exe");

    [SkippableFact]
    public void Extension_exclusion_can_be_added_and_removed() =>
        RunRoundTrip(ExclusionKind.Extension, $".wdt{Guid.NewGuid():N}".Substring(0, 12));

    [SkippableFact]
    public void IpAddress_exclusion_can_be_added_and_removed()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        RunRoundTrip(ExclusionKind.IpAddress, $"10.{bytes[0]}.{bytes[1]}.{bytes[2]}");
    }

    [SkippableFact]
    public void Removing_a_value_that_is_not_excluded_does_not_throw()
    {
        RequireElevatedDefender();

        var value = $@"C:\WdtApiTest\NotPresent-{Guid.NewGuid():N}";
        DefenderExclusionManager.RemoveExclusion(ExclusionKind.Path, value);

        Assert.DoesNotContain(value, DefenderExclusionManager.GetExclusions().Paths);
    }

    private static void RunRoundTrip(ExclusionKind kind, string value)
    {
        RequireElevatedDefender();

        // Clean slate, in case a previous run crashed before its cleanup.
        TestEnvironment.RemoveIfPresent(kind, value);

        try
        {
            DefenderExclusionManager.AddExclusion(kind, value);
            Assert.Contains(value, DefenderExclusionManager.GetExclusions().For(kind));
        }
        finally
        {
            DefenderExclusionManager.RemoveExclusion(kind, value);
        }

        Assert.DoesNotContain(value, DefenderExclusionManager.GetExclusions().For(kind));
    }

    private static void RequireElevatedDefender()
    {
        Skip.IfNot(TestEnvironment.IsElevated,
            "This test changes real Defender settings and must run from an elevated test host " +
            "(restart 'dotnet test' / Visual Studio as administrator).");
        Skip.IfNot(TestEnvironment.IsDefenderAvailable(out var unavailable), unavailable);
    }
}
