using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace WindowsDefenderPerformanceTool;

/// <summary>
/// Adds Windows Defender exclusions via Add-MpPreference. Shared by the top-processes
/// grid (process exclusions) and the scan-time treemap (path exclusions).
/// </summary>
public static class DefenderExclusions
{
    /// <summary>Excludes a process (image name or full path) from scanning, after confirmation.</summary>
    public static void AddProcessExclusion(string processName)
    {
        if (!ConfirmExclusion("process", processName,
                "Files touched by an excluded process are no longer scanned."))
            return;

        var escaped = processName.Replace("'", "''");
        RunAddMpPreference($"-ExclusionProcess '{escaped}'", processName);
    }

    /// <summary>Excludes a file or directory path from scanning, after confirmation.</summary>
    public static void AddPathExclusion(string path)
    {
        if (!ConfirmExclusion("path", path,
                "Files under an excluded path are no longer scanned."))
            return;

        var escaped = path.Replace("'", "''");
        RunAddMpPreference($"-ExclusionPath '{escaped}'", path);
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

    private static void RunAddMpPreference(string preferenceArgument, string target)
    {
        var arguments = $"-NoProfile -Command \"Add-MpPreference {preferenceArgument}\"";
        var isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

        try
        {
            var psi = new ProcessStartInfo("powershell.exe", arguments);
            if (isAdmin)
            {
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using var process = Process.Start(psi);
                process!.WaitForExit();
                if (process.ExitCode == 0)
                {
                    MessageBox.Show($"Added to Defender exclusions:\n{target}",
                        "Add Defender Exclusion", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        $"Add-MpPreference failed (exit code {process.ExitCode}).\n" +
                        "Try running the tool as administrator.",
                        "Add Defender Exclusion", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                // Not elevated — relaunch the command with a UAC prompt.
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
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
