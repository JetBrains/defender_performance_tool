using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowsDefenderPerformanceTool;

/// <summary>
/// Immutable node in the scanned-files tree. Directory nodes aggregate the scan time of
/// everything below them, so a treemap can show where scan activity is concentrated
/// (e.g. that most of the time went into C:\work\repository).
/// </summary>
public sealed class ScanTreeNode
{
    /// <summary>Display segment, e.g. "C:", "work\repository" (compressed chain) or "file.dll".</summary>
    public string Name { get; }

    public string FullPath { get; }

    /// <summary>Total scan time of this node including everything below it.</summary>
    public double TotalSeconds { get; }

    /// <summary>Children sorted by <see cref="TotalSeconds"/> descending. Empty for file leaves.</summary>
    public IReadOnlyList<ScanTreeNode> Children { get; }

    private ScanTreeNode(string name, string fullPath, double totalSeconds, IReadOnlyList<ScanTreeNode> children)
    {
        Name = name;
        FullPath = fullPath;
        TotalSeconds = totalSeconds;
        Children = children;
    }

    /// <summary>
    /// Builds the tree from per-file totals (milliseconds). Single-child directory chains are
    /// merged (C:\work containing only "repository" becomes one "work\repository" node), which
    /// keeps the treemap shallow enough to read. The root has an empty name and one child per
    /// top-level path segment (typically drive letters).
    /// </summary>
    public static ScanTreeNode Build(IReadOnlyDictionary<string, double> fileTotalsMs)
    {
        var root = new Builder("", "");

        foreach (var kvp in fileTotalsMs)
        {
            var node = root;
            node.TotalMs += kvp.Value;
            var path = "";
            foreach (var segment in SplitPath(kvp.Key))
            {
                path = path.Length == 0 ? segment : path + "\\" + segment;
                if (!node.Children.TryGetValue(segment, out var child))
                {
                    child = new Builder(segment, path);
                    node.Children.Add(segment, child);
                }
                child.TotalMs += kvp.Value;
                node = child;
            }
        }

        return ToNode(root, compress: false);
    }

    private static ScanTreeNode ToNode(Builder builder, bool compress)
    {
        if (compress)
        {
            // Merge single-child chains: "work" -> "repository" becomes "work\repository".
            while (builder.Children.Count == 1)
            {
                var only = builder.Children.Values.First();
                builder.Name += "\\" + only.Name;
                builder.FullPath = only.FullPath;
                builder.Children = only.Children;
            }
        }

        var children = builder.Children.Values
            .OrderByDescending(c => c.TotalMs)
            .Select(c => ToNode(c, compress: true))
            .ToList();

        return new ScanTreeNode(builder.Name, builder.FullPath, builder.TotalMs / 1000.0, children);
    }

    private static readonly char[] Separators = { '\\', '/' };

    private static string[] SplitPath(string path)
    {
        // ETW kernel paths may carry a "\??\" object-manager prefix — strip it.
        var p = path.StartsWith("\\??\\", StringComparison.Ordinal) ? path.Substring(4) : path;
        var parts = p.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts : new[] { p };
    }

    private sealed class Builder
    {
        public string Name;
        public string FullPath;
        public double TotalMs;
        public Dictionary<string, Builder> Children = new(StringComparer.OrdinalIgnoreCase);

        public Builder(string name, string fullPath)
        {
            Name = name;
            FullPath = fullPath;
        }
    }
}
