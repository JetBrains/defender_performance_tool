using System;
using System.Linq;
using Xunit;

namespace DefenderPerformanceTool.Tests.UiAutomation;

/// <summary>
/// End-to-end persistence tests for the exclusion manager page.
///
/// Each test launches the real application (elevated), opens the exclusion manager,
/// adds one exclusion through the UI, closes the app, relaunches it and verifies the
/// exclusion survived the restart — then removes it again via the Defender API.
///
/// These tests modify the machine's real Microsoft Defender configuration and must run
/// from an elevated test host (dotnet test / vstest started as administrator);
/// otherwise they are skipped.
/// </summary>
[Trait("Category", "RequiresElevation")]
[Collection(UiAutomationCollection.Name)]
public class ExclusionPersistenceUiTests
{
    [SkippableFact]
    public void Path_exclusion_is_preserved_across_app_restarts() =>
        RunPersistenceScenario(ExclusionKind.Path, "Paths",
            $@"C:\WdtUiTest\{Guid.NewGuid():N}");

    [SkippableFact]
    public void Process_exclusion_is_preserved_across_app_restarts() =>
        RunPersistenceScenario(ExclusionKind.Process, "Processes",
            $"wdt-ui-test-{Guid.NewGuid():N}.exe");

    [SkippableFact]
    public void Extension_exclusion_is_preserved_across_app_restarts() =>
        RunPersistenceScenario(ExclusionKind.Extension, "Extensions",
            $".wdt{Guid.NewGuid():N}".Substring(0, 12));

    [SkippableFact]
    public void IpAddress_exclusion_is_preserved_across_app_restarts()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        RunPersistenceScenario(ExclusionKind.IpAddress, "IP Addresses",
            $"10.{bytes[0]}.{bytes[1]}.{bytes[2]}");
    }

    private static void RunPersistenceScenario(ExclusionKind kind, string tabTitlePrefix, string value)
    {
        Skip.IfNot(TestEnvironment.IsElevated,
            "This test changes real Defender settings and must run from an elevated test host " +
            "(restart 'dotnet test' / Visual Studio as administrator).");
        Skip.IfNot(TestEnvironment.IsDefenderAvailable(out var unavailable), unavailable);

        // Clean slate, in case a previous run crashed before its cleanup.
        TestEnvironment.RemoveIfPresent(kind, value);

        try
        {
            // --- First instance: add the exclusion purely through the UI. ---
            using (var app = new AppUnderTest())
            {
                app.Launch();
                using var manager = app.OpenExclusionManager();
                manager.SelectTab(tabTitlePrefix);
                manager.AddExclusion(value);

                Assert.Contains(value, manager.GetListedValues());
            }

            // Cross-check straight against Defender, independent of the UI.
            Assert.Contains(value, DefenderExclusionManager.GetExclusions().For(kind));

            // --- Second instance: the exclusion must still be there and be displayed. ---
            using (var app = new AppUnderTest())
            {
                app.Launch();
                using var manager = app.OpenExclusionManager();
                manager.SelectTab(tabTitlePrefix);

                Assert.Contains(value, manager.WaitForListedValue(value));
            }
        }
        finally
        {
            TestEnvironment.RemoveIfPresent(kind, value);
        }

        Assert.DoesNotContain(value, DefenderExclusionManager.GetExclusions().For(kind));
    }
}
