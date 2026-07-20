using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsDefenderPerformanceTool;

/// <summary>
/// Converts NT device paths as reported by Defender ETW events
/// (<c>\Device\HarddiskVolume3\Windows\foo.dll</c>) into DOS drive-letter paths
/// (<c>C:\Windows\foo.dll</c>) so they can be used with Explorer, Defender exclusions,
/// the clipboard, etc. The volume → drive mapping is queried once via
/// <c>QueryDosDevice</c> and cached for the process lifetime.
/// </summary>
internal static class DevicePathConverter
{
    private const string DevicePrefix = @"\Device\";

    // Maps e.g. "\Device\HarddiskVolume3" → "C:"
    private static readonly Lazy<IReadOnlyDictionary<string, string>> DeviceToDrive =
        new(BuildDeviceMap);

    /// <summary>
    /// Returns <paramref name="path"/> as a DOS path when it starts with a known
    /// <c>\Device\…</c> volume; otherwise returns it unchanged (already a DOS path,
    /// network redirector, removed disk, …).
    /// </summary>
    public static string ToDosPath(string path)
    {
        if (!path.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase))
            return path;

        return ToDosPath(path, DeviceToDrive.Value);
    }

    // Separated from the device-map lookup so the matching logic can be unit-tested.
    internal static string ToDosPath(string path, IReadOnlyDictionary<string, string> deviceToDrive)
    {
        foreach (var kvp in deviceToDrive)
        {
            var device = kvp.Key;
            if (path.Length > device.Length
                && path.StartsWith(device, StringComparison.OrdinalIgnoreCase)
                && path[device.Length] == '\\')
            {
                return kvp.Value + path.Substring(device.Length);
            }
        }
        return path;
    }

    private static IReadOnlyDictionary<string, string> BuildDeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            var letter = drive.Name.TrimEnd('\\'); // "C:"
            var buffer = new StringBuilder(260);
            if (QueryDosDevice(letter, buffer, (uint)buffer.Capacity) == 0) continue;
            var device = buffer.ToString(); // e.g. "\Device\HarddiskVolume3"
            if (device.Length > 0)
                map[device] = letter;
        }
        return map;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);
}
