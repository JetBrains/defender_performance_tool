using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;

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
/// C# port of the PowerShell Defender exclusion cmdlets (Get/Add/Remove-MpPreference).
///
/// Those cmdlets are thin generated wrappers over the WMIv2 class
/// <c>MSFT_MpPreference</c> in the <c>root\Microsoft\Windows\Defender</c> namespace:
/// reading = the singleton instance's ExclusionPath / ExclusionProcess /
/// ExclusionExtension / ExclusionIpAddress string-array properties;
/// writing = the instance methods <c>Add</c> and <c>Remove</c> taking the same-named
/// parameters. This class talks to that provider directly via System.Management and
/// falls back to invoking powershell.exe when the provider call itself fails.
///
/// Requires elevation to read real values and to modify the lists.
/// </summary>
public static class DefenderExclusionManager
{
    private const string WmiNamespace = @"root\Microsoft\Windows\Defender";
    private const string WmiClass = "MSFT_MpPreference";
    private const string HiddenSentinel = "N/A:"; // "N/A: Must be an administrator to view exclusions"

    /// <summary>Maps a kind to the MSFT_MpPreference property / Add-Remove parameter name.</summary>
    public static string PropertyName(ExclusionKind kind) => kind switch
    {
        ExclusionKind.Path => "ExclusionPath",
        ExclusionKind.Process => "ExclusionProcess",
        ExclusionKind.Extension => "ExclusionExtension",
        ExclusionKind.IpAddress => "ExclusionIpAddress",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Reads all four exclusion lists. Throws when the Defender provider is unavailable.</summary>
    public static ExclusionSnapshot GetExclusions()
    {
        return WithPreference(pref =>
        {
            var paths = ReadList(pref, ExclusionKind.Path, out var hidden);
            return new ExclusionSnapshot
            {
                Paths = paths,
                Processes = ReadList(pref, ExclusionKind.Process, out _),
                Extensions = ReadList(pref, ExclusionKind.Extension, out _),
                IpAddresses = ReadList(pref, ExclusionKind.IpAddress, out _),
                HiddenFromLocalUsers = hidden
            };
        });
    }

    /// <summary>Adds a value to the given exclusion list. Throws on failure.</summary>
    public static void AddExclusion(ExclusionKind kind, string value) =>
        InvokeWithFallback("Add", "Add-MpPreference", kind, value);

    /// <summary>Removes a value from the given exclusion list. Throws on failure.</summary>
    public static void RemoveExclusion(ExclusionKind kind, string value) =>
        InvokeWithFallback("Remove", "Remove-MpPreference", kind, value);

    // --- WMI provider access ---

    private static T WithPreference<T>(Func<ManagementObject, T> action)
    {
        var scope = new ManagementScope(WmiNamespace);
        scope.Connect();
        using var wmiClass = new ManagementClass(scope, new ManagementPath(WmiClass), null);
        using var instances = wmiClass.GetInstances();
        var pref = instances.Cast<ManagementObject>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Windows Defender preferences are not available. " +
                "Is Microsoft Defender Antivirus active on this machine?");
        return action(pref);
    }

    private static IReadOnlyList<string> ReadList(ManagementObject pref, ExclusionKind kind, out bool hidden)
    {
        var values = pref[PropertyName(kind)] as string[] ?? Array.Empty<string>();
        hidden = values.Any(v => v.StartsWith(HiddenSentinel, StringComparison.Ordinal));
        return hidden
            ? Array.Empty<string>()
            : values.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
    }

    private static void InvokeWmi(string method, ExclusionKind kind, string value)
    {
        WithPreference<object?>(pref =>
        {
            using var inParams = pref.GetMethodParameters(method);
            inParams[PropertyName(kind)] = new[] { value };
            using var outParams = pref.InvokeMethod(method, inParams, null);
            var returnValue = Convert.ToInt64(outParams?["ReturnValue"] ?? 0);
            if (returnValue != 0)
                throw new InvalidOperationException(
                    $"Defender rejected the change (return code 0x{returnValue:X8}).");
            return null;
        });
    }

    // --- PowerShell fallback (same cmdlets the ported code replaces) ---

    private static void InvokeWithFallback(string wmiMethod, string cmdlet, ExclusionKind kind, string value)
    {
        try
        {
            InvokeWmi(wmiMethod, kind, value);
        }
        catch (Exception wmiError) when (wmiError is ManagementException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException
                                           or System.Runtime.InteropServices.COMException)
        {
            RunPowerShell(cmdlet, kind, value, wmiError);
        }
    }

    private static void RunPowerShell(string cmdlet, ExclusionKind kind, string value, Exception wmiError)
    {
        var escaped = value.Replace("'", "''");
        var arguments = $"-NoProfile -Command \"{cmdlet} -{PropertyName(kind)} '{escaped}'\"";
        var psi = new ProcessStartInfo("powershell.exe", arguments)
        {
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start powershell.exe.");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? wmiError.Message : stderr.Trim();
            throw new InvalidOperationException(
                $"{cmdlet} failed (exit code {process.ExitCode}). " +
                "Make sure the tool is running as administrator.\n\n" + detail);
        }
    }
}
