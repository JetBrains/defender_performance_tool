using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;

namespace WindowsDefenderPerformanceTool;

/// <summary>The four exclusion lists supported by Windows Defender.</summary>
public enum ExclusionKind
{
    Path,
    Process,
    Extension,
    IpAddress
}

/// <summary>Immutable snapshot of all Defender exclusion lists.</summary>
public sealed class ExclusionSnapshot
{
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Processes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Extensions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> IpAddresses { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True when Defender refused to reveal the lists ("N/A: Must be an administrator…"),
    /// e.g. when the caller is not elevated and HideExclusionsFromLocalUsers is in effect.
    /// </summary>
    public bool HiddenFromLocalUsers { get; init; }

    public int TotalCount => Paths.Count + Processes.Count + Extensions.Count + IpAddresses.Count;

    public IReadOnlyList<string> For(ExclusionKind kind) => kind switch
    {
        ExclusionKind.Path => Paths,
        ExclusionKind.Process => Processes,
        ExclusionKind.Extension => Extensions,
        ExclusionKind.IpAddress => IpAddresses,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

/// <summary>
/// Wraps the PowerShell Defender exclusion cmdlets (Get/Add/Remove-MpPreference).
/// Requires elevation to read real values and to modify the lists.
/// </summary>
public static class DefenderExclusionManager
{
    private const string HiddenSentinel = "N/A:"; // "N/A: Must be an administrator to view exclusions"

    /// <summary>True when the current process runs elevated (required to read/modify exclusions).</summary>
    public static bool IsRunningAsAdmin { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Maps a kind to the MpPreference property / cmdlet parameter name.</summary>
    public static string PropertyName(ExclusionKind kind) => kind switch
    {
        ExclusionKind.Path => "ExclusionPath",
        ExclusionKind.Process => "ExclusionProcess",
        ExclusionKind.Extension => "ExclusionExtension",
        ExclusionKind.IpAddress => "ExclusionIpAddress",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Builds the powershell.exe argument list that runs the given Defender cmdlet.</summary>
    public static string BuildPowerShellArguments(string cmdlet, ExclusionKind kind, string value)
    {
        var escaped = value.Replace("'", "''");
        return $"-NoProfile -Command \"{cmdlet} -{PropertyName(kind)} '{escaped}'\"";
    }

    /// <summary>Reads all four exclusion lists. Throws when the Defender provider is unavailable.</summary>
    public static ExclusionSnapshot GetExclusions() => new()
    {
        Paths = ReadList(ExclusionKind.Path, out var hidden),
        Processes = ReadList(ExclusionKind.Process, out _),
        Extensions = ReadList(ExclusionKind.Extension, out _),
        IpAddresses = ReadList(ExclusionKind.IpAddress, out _),
        HiddenFromLocalUsers = hidden
    };

    /// <summary>Adds a value to the given exclusion list. Throws on failure.</summary>
    public static void AddExclusion(ExclusionKind kind, string value) =>
        RunPowerShell(BuildPowerShellArguments("Add-MpPreference", kind, value));

    /// <summary>Removes a value from the given exclusion list. Throws on failure.</summary>
    public static void RemoveExclusion(ExclusionKind kind, string value) =>
        RunPowerShell(BuildPowerShellArguments("Remove-MpPreference", kind, value));

    private static IReadOnlyList<string> ReadList(ExclusionKind kind, out bool hidden)
    {
        var output = RunPowerShell($"-NoProfile -Command \"(Get-MpPreference).{PropertyName(kind)}\"");
        var values = output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(v => v.Trim())
                           .Where(v => v.Length > 0)
                           .ToArray();
        hidden = values.Any(v => v.StartsWith(HiddenSentinel, StringComparison.Ordinal));
        return hidden ? Array.Empty<string>() : values;
    }

    /// <summary>Runs powershell.exe with the given arguments and returns stdout. Throws on failure.</summary>
    private static string RunPowerShell(string arguments)
    {
        var psi = new ProcessStartInfo("powershell.exe", arguments)
        {
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start powershell.exe.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Defender command failed (exit code {process.ExitCode}). " +
                "Make sure the tool is running as administrator.\n\n" + stderr.Trim());

        return stdout;
    }
}
