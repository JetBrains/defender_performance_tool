using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
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
/// Wraps the PowerShell Defender exclusion cmdlets (Get/Add/Remove-MpPreference) by hosting
/// the PowerShell engine in-process through System.Management.Automation — the same API
/// that runs those cmdlets' PowerShell scripts underneath. No powershell.exe process is
/// ever spawned, not even as a fallback.
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

    /// <summary>Reads all four exclusion lists. Throws when the Defender provider is unavailable.</summary>
    public static ExclusionSnapshot GetExclusions()
    {
        using var ps = CreatePowerShell();
        ps.AddCommand("Get-MpPreference");
        var preference = Invoke(ps, "Get-MpPreference").FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Windows Defender preferences are not available. " +
                "Is Microsoft Defender Antivirus active on this machine?");

        var paths = ReadList(preference, ExclusionKind.Path, out var hidden);
        return new ExclusionSnapshot
        {
            Paths = paths,
            Processes = ReadList(preference, ExclusionKind.Process, out _),
            Extensions = ReadList(preference, ExclusionKind.Extension, out _),
            IpAddresses = ReadList(preference, ExclusionKind.IpAddress, out _),
            HiddenFromLocalUsers = hidden
        };
    }

    /// <summary>Adds a value to the given exclusion list. Throws on failure.</summary>
    public static void AddExclusion(ExclusionKind kind, string value) =>
        InvokePreferenceChange("Add-MpPreference", kind, value);

    /// <summary>Removes a value from the given exclusion list. Throws on failure.</summary>
    public static void RemoveExclusion(ExclusionKind kind, string value) =>
        InvokePreferenceChange("Remove-MpPreference", kind, value);

    private static void InvokePreferenceChange(string cmdlet, ExclusionKind kind, string value)
    {
        using var ps = CreatePowerShell();
        // Pass the value as a real cmdlet parameter — never as interpolated script text.
        ps.AddCommand(cmdlet).AddParameter(PropertyName(kind), new[] { value });
        Invoke(ps, cmdlet);
    }

    private static PowerShell CreatePowerShell() => PowerShell.Create();

    private static IReadOnlyList<PSObject> Invoke(PowerShell ps, string what)
    {
        try
        {
            var results = ps.Invoke();
            if (ps.HadErrors)
            {
                var details = string.Join("\n", ps.Streams.Error
                    .Select(e => e.Exception?.Message ?? e.ToString())
                    .Where(m => !string.IsNullOrWhiteSpace(m)));
                throw new InvalidOperationException(
                    $"{what} failed. Make sure the tool is running as administrator.\n\n{details}");
            }
            return results;
        }
        catch (RuntimeException ex)
        {
            throw new InvalidOperationException(
                $"{what} failed. Make sure the tool is running as administrator.\n\n{ex.Message}", ex);
        }
    }

    private static IReadOnlyList<string> ReadList(PSObject preference, ExclusionKind kind, out bool hidden)
    {
        var values = AsStringList(preference.Properties[PropertyName(kind)]?.Value);
        hidden = values.Any(v => v.StartsWith(HiddenSentinel, StringComparison.Ordinal));
        return hidden
            ? Array.Empty<string>()
            : values.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
    }

    private static string[] AsStringList(object? value) => value switch
    {
        null => Array.Empty<string>(),
        string single => new[] { single },
        IEnumerable items => items.Cast<object>().Select(i => i?.ToString() ?? "").ToArray(),
        _ => new[] { value.ToString() ?? "" }
    };
}
