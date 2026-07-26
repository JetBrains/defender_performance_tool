using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace WindowsDefenderPerformanceTool;

/// <summary>
/// Adds Windows Defender exclusions, with confirmation. Shared by the top-processes
/// grid (process exclusions) and the scan-time treemap (path exclusions).
/// Delegates to <see cref="DefenderExclusionManager"/> (a C# port of Add-MpPreference).
/// </summary>
public static class DefenderExclusions
{
    /// <summary>Excludes a process (image name or full path) from scanning, after confirmation.</summary>
    public static void AddProcessExclusion(string processName)
    {
        if (!ConfirmExclusion("process", processName,
                "Files touched by an excluded process are no longer scanned."))
            return;

        AddExclusion(ExclusionKind.Process, processName);
    }

    /// <summary>Excludes a file or directory path from scanning, after confirmation.</summary>
    public static void AddPathExclusion(string path)
    {
        if (!ConfirmExclusion("path", path,
                "Files under an excluded path are no longer scanned."))
            return;

        AddExclusion(ExclusionKind.Path, path);
    }

    private static bool ConfirmExclusion(string kind, string target, string consequence)
    {
        var confirm = MessageBox.Show(
            $"Add this {kind} to the Windows Defender exclusion list?\n\n{target}\n\n{consequence}\n\n" +
            "Before you define exclusions, review Exclusions in Microsoft Defender Antivirus page. " +
            "Every exclusion is a protection gap that lowers your defenses, so use exclusions sparingly.\n" +
            "https://learn.microsoft.com/en-us/defender-endpoint/microsoft-defender-antivirus-exclusions-overview",
            "Add Defender Exclusion", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return confirm == MessageBoxResult.Yes;
    }

    private static void AddExclusion(ExclusionKind kind, string target)
    {
        var isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

        if (isAdmin)
        {
            try
            {
                DefenderExclusionManager.AddExclusion(kind, target);
                MessageBox.Show($"Added to Defender exclusions:\n{target}",
                    "Add Defender Exclusion", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to add the exclusion:\n\n{ex.Message}",
                    "Add Defender Exclusion", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            // Not elevated — the WMI provider would refuse the change, so relaunch the
            // equivalent PowerShell cmdlet with a UAC prompt instead.
            RunAddMpPreferenceElevated(kind, target);
        }
    }

    private static void RunAddMpPreferenceElevated(ExclusionKind kind, string target)
    {
        var escaped = target.Replace("'", "''");
        var arguments = $"-NoProfile -Command \"Add-MpPreference -{DefenderExclusionManager.PropertyName(kind)} '{escaped}'\"";

        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe", arguments)
            {
                Verb = "runas",
                UseShellExecute = true
            });
        }
        catch (Win32Exception)
        {
            // User cancelled the UAC prompt — nothing to do.
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to add the exclusion:\n\n{ex.Message}",
                "Add Defender Exclusion", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
