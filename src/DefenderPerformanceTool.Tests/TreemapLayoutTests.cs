using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Xunit;

namespace DefenderPerformanceTool.Tests;

public class TreemapLayoutTests
{
    [Theory]
    [InlineData(0, 500, 300)]
    [InlineData(1, 500, 300)]
    [InlineData(5, 200, 100)]
    [InlineData(20, 800, 600)]
    public void Squarify_ProducesInBoundsNonOverlappingRects(int count, double width, double height)
    {
        var rng = new Random(42 + count);
        var weights = Enumerable.Range(0, count)
            .Select(_ => Math.Pow(rng.NextDouble(), 3) * 1000 + 0.01)
            .ToList();
        var bounds = new Rect(0, 0, width, height);

        var rects = TreemapControl.Squarify(weights, bounds);

        Assert.Equal(count, rects.Length);
        const double eps = 1e-6;

        for (int i = 0; i < count; i++)
        {
            var r = rects[i];
            Assert.True(r.X >= -eps, $"rect {i} left >= 0");
            Assert.True(r.Y >= -eps, $"rect {i} top >= 0");
            Assert.True(r.Right <= width + eps, $"rect {i} right <= width");
            Assert.True(r.Bottom <= height + eps, $"rect {i} bottom <= height");
            Assert.True(r.Width >= 0, $"rect {i} width >= 0");
            Assert.True(r.Height >= 0, $"rect {i} height >= 0");

            for (int j = i + 1; j < count; j++)
            {
                var o = rects[j];
                double overlapW = Math.Min(r.Right, o.Right) - Math.Max(r.X, o.X);
                double overlapH = Math.Min(r.Bottom, o.Bottom) - Math.Max(r.Y, o.Y);
                Assert.True(overlapW <= eps || overlapH <= eps,
                    $"rects {i} and {j} overlap");
            }
        }

        double sumArea = rects.Sum(r => r.Width * r.Height);
        if (count > 0)
            Assert.Equal(width * height, sumArea, 0.5);
    }

    [Fact]
    public void Squarify_WorstAspectRatioIsReasonable()
    {
        var weights = new List<double> { 100, 90, 80, 70, 60, 50, 40, 30 };
        var bounds = new Rect(0, 0, 500, 300);

        var rects = TreemapControl.Squarify(weights, bounds);

        double worstAspect = rects
            .Where(r => r.Width > 0 && r.Height > 0)
            .Max(r => Math.Max(r.Width / r.Height, r.Height / r.Width));

        Assert.True(worstAspect < 8, $"worst aspect ratio {worstAspect} should be < 8");
    }

    [Fact]
    public void Squarify_DegenerateBoundsDoesNotCrash()
    {
        var weights = new List<double> { 1, 1 };
        var bounds = new Rect(0, 0, 0, 100);
        var rects = TreemapControl.Squarify(weights, bounds);
        Assert.Equal(2, rects.Length);
    }
}
