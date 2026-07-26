using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace WindowsDefenderPerformanceTool;

/// <summary>
/// UI helpers for adding Windows Defender exclusions, with confirmation. Shared by the
/// top-processes grid (process exclusions), the scan-time treemap (path exclusions) and
/// the exclusion manager dialog. Delegates to <see cref="DefenderExclusionManager"/>
/// (a C# port of Add-MpPreference).
/// </summary>
public static class DefenderExclusions
{
    /// <summary>Excludes a process (image name or full path) from scanning, after confirmation.</summary>
    public static void AddProcessExclusion(string processName) =>
        AddWithConfirmation(ExclusionKind.Process, "process", processName,
            "Files touched by an excluded process are no longer scanned.");

    /// <summary>Excludes a file or directory path from scanning, after confirmation.</summary>
    public static void AddPathExclusion(string path) =>
        AddWithConfirmation(ExclusionKind.Path, "path", path,
            "Files under an excluded path are no longer scanned.");

    private static void AddWithConfirmation(ExclusionKind kind, string kindLabel, string target, string consequence)
    {
        if (!ConfirmExclusion(kindLabel, target, consequence))
            return;

        AddExclusion(kind, target);
    }

    /// <summary>Asks the user to confirm adding an exclusion; returns true when confirmed.</summary>
    public static bool ConfirmExclusion(string kindLabel, string target, string? consequence = null)
    {
        var message = $"Add this {kindLabel} to the Windows Defender exclusion list?\n\n{target}\n\n";
        if (!string.IsNullOrEmpty(consequence))
            message += consequence + "\n\n";
        message +=
            "Before you define exclusions, review Exclusions in Microsoft Defender Antivirus page. " +
            "Every exclusion is a protection gap that lowers your defenses, so use exclusions sparingly.\n" +
            "https://learn.microsoft.com/en-us/defender-endpoint/microsoft-defender-antivirus-exclusions-overview";

        var confirm = MessageBox.Show(message,
            "Add Defender Exclusion", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return confirm == MessageBoxResult.Yes;
    }

    private static void AddExclusion(ExclusionKind kind, string target)
    {
        if (DefenderExclusionManager.IsRunningAsAdmin)
        {
            try
            {
                DefenderExclusionManager.AddExclusion(kind, target);
                MessageBox.Show($"Added to Defender exclusions:\n{target}",
                    "Add Defender Exclusion", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowAddError(ex);
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
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe",
                    DefenderExclusionManager.BuildPowerShellArguments("Add-MpPreference", kind, target))
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
            ShowAddError(ex);
        }
    }

    private static void ShowAddError(Exception ex) =>
        MessageBox.Show($"Failed to add the exclusion:\n\n{ex.Message}",
            "Add Defender Exclusion", MessageBoxButton.OK, MessageBoxImage.Error);
}
