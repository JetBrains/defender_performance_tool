using System.Collections.Generic;
using Xunit;

namespace DefenderPerformanceTool.Tests;

public class ScanTreeNodeTests
{
    [Fact]
    public void Build_AggregatesTotalsPerDirectoryAndSortsChildren()
    {
        var totals = new Dictionary<string, double>
        {
            [@"C:\work\repository\src\a.cs"] = 5000,
            [@"C:\work\repository\src\b.cs"] = 3000,
            [@"C:\work\repository\README.md"] = 1000,
            [@"C:\Windows\System32\kernel32.dll"] = 2000,
            [@"D:\media\movie.mkv"] = 4000,
        };

        var root = ScanTreeNode.Build(totals);

        Assert.Equal(15.0, root.TotalSeconds, 1e-9);
        Assert.Equal("", root.Name);
        Assert.Equal(2, root.Children.Count);

        // C: is largest with 11s total.
        Assert.Equal("C:", root.Children[0].Name);
        Assert.Equal(11.0, root.Children[0].TotalSeconds, 1e-9);

        // D: has only one scanned chain, so it compresses all the way to the file.
        Assert.Equal(@"D:\media\movie.mkv", root.Children[1].Name);
        Assert.Equal(4.0, root.Children[1].TotalSeconds, 1e-9);
    }

    [Fact]
    public void Build_CompressesSingleChildChains()
    {
        var totals = new Dictionary<string, double>
        {
            [@"C:\work\repository\src\a.cs"] = 100,
            [@"C:\work\repository\sub\b.cs"] = 50,
        };

        var root = ScanTreeNode.Build(totals);
        var c = Assert.Single(root.Children);
        Assert.Equal("C:", c.Name);

        var repo = Assert.Single(c.Children);
        Assert.Equal(@"work\repository", repo.Name);
        Assert.Equal(@"C:\work\repository", repo.FullPath);
        Assert.Equal(0.15, repo.TotalSeconds, 1e-9);
        Assert.Equal(2, repo.Children.Count);
    }

    [Fact]
    public void Build_StripsObjectManagerPrefixFromEtwPaths()
    {
        var totals = new Dictionary<string, double>
        {
            [@"\??\C:\work\repository\obj\c.obj"] = 500,
            [@"C:\work\repository\a.cs"] = 1000,
        };

        var root = ScanTreeNode.Build(totals);
        var c = root.Children[0];
        var repo = c.Children[0];

        Assert.Equal(@"work\repository", repo.Name);
        Assert.Equal(2, repo.Children.Count);
        Assert.Equal(1.5, repo.TotalSeconds, 1e-9);

        var cObj = Assert.Single(repo.Children, n => n.Name == @"obj\c.obj");
        Assert.Equal(0.5, cObj.TotalSeconds, 1e-9);
    }

    [Fact]
    public void Build_HandlesEmptyInput()
    {
        var root = ScanTreeNode.Build(new Dictionary<string, double>());
        Assert.Empty(root.Children);
        Assert.Equal(0, root.TotalSeconds);
    }

    [Fact]
    public void Build_HandlesPathWithoutSeparators()
    {
        var root = ScanTreeNode.Build(new Dictionary<string, double> { ["no-separators"] = 100 });
        var child = Assert.Single(root.Children);
        Assert.Equal("no-separators", child.Name);
        Assert.Equal(0.1, child.TotalSeconds, 1e-9);
    }
}
