using System;
using System.Windows;

namespace WindowsDefenderPerformanceTool;

/// <summary>
/// UI helpers for adding Windows Defender exclusions, with confirmation. Shared by the
/// top-processes grid (process exclusions), the scan-time treemap (path exclusions) and
/// the exclusion manager dialog. Delegates to <see cref="DefenderExclusionManager"/>
/// (which hosts the Add-MpPreference cmdlet in-process via System.Management.Automation).
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

        return ShowConfirmation(message, "Add Defender Exclusion");
    }

    /// <summary>
    /// Shows the confirmation prompt and returns true when the user answers Yes.
    /// Replaceable in tests (a real MessageBox cannot be automated from the test host).
    /// </summary>
    internal static Func<string, string, bool> ShowConfirmation { get; set; } =
        (message, title) => MessageBox.Show(message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

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
            // The in-process PowerShell engine cannot elevate itself, and we never spawn
            // powershell.exe — ask the user to restart the tool with administrator rights.
            MessageBox.Show(
                "Adding a Defender exclusion requires administrator privileges.\n\n" +
                "Please restart Windows Defender Performance Tool as administrator and try again.",
                "Add Defender Exclusion", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void ShowAddError(Exception ex) =>
        MessageBox.Show($"Failed to add the exclusion:\n\n{ex.Message}",
            "Add Defender Exclusion", MessageBoxButton.OK, MessageBoxImage.Error);
}
