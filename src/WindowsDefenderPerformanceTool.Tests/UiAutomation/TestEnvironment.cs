using System;
using System.Security.Principal;

namespace WindowsDefenderPerformanceTool.Tests.UiAutomation;

/// <summary>Environment gates for tests that drive the real app against real Defender settings.</summary>
public static class TestEnvironment
{
    /// <summary>True when the test host itself runs elevated (children inherit the elevation).</summary>
    public static bool IsElevated =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>True when the Defender WMI provider answers a real exclusion query.</summary>
    public static bool IsDefenderAvailable(out string reason)
    {
        try
        {
            DefenderExclusionManager.GetExclusions();
            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Windows Defender preferences are not queryable on this machine: {ex.Message}";
            return false;
        }
    }

    /// <summary>Removes the value if present, swallowing "not present" failures. Used for setup/cleanup.</summary>
    public static void RemoveIfPresent(ExclusionKind kind, string value)
    {
        try
        {
            DefenderExclusionManager.RemoveExclusion(kind, value);
        }
        catch
        {
            // Best effort: the value may simply not be in the list.
        }
    }
}
