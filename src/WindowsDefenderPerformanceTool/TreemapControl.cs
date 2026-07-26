using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowsDefenderPerformanceTool;

/// <summary>
/// Renders a <see cref="ScanTreeNode"/> hierarchy as a squarified treemap: rectangle area is
/// proportional to total scan time, nesting follows the directory structure. Each top-level
/// segment (typically a drive) gets its own hue, lightened with depth. Rebuilt whenever
/// <see cref="Root"/> or the control size changes.
///
/// Tiles are interactive: right-clicking opens a context menu (copy path, open in Explorer,
/// add to Defender exclusions, zoom in/out), and double-clicking a directory zooms into it.
/// While zoomed, a breadcrumb overlay at the top left jumps back to the overview.
/// Tiles are highlighted on hover. While the mouse is over the control, incoming data
/// updates are paused (so the view doesn't change under the cursor or lose the zoom);
/// this is indicated by an orange frame with a pause icon.
/// </summary>
public sealed class TreemapControl : Canvas
{
    private const int MaxDepth = 8;
    private const int MaxChildrenPerNode = 16;
    private const int MaxRenderedNodes = 400;
    private const double MinNodeFraction = 0.002; // of total scanned time
    private const double HeaderHeight = 16;
    private const double MinHeaderWidth = 48;
    private const double MinHeaderBodyHeight = 24;

    // Tableau 10 — distinct hues per top-level directory.
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x4E, 0x79, 0xA7),
        Color.FromRgb(0xF2, 0x8E, 0x2B),
        Color.FromRgb(0xE1, 0x57, 0x59),
        Color.FromRgb(0x76, 0xB7, 0xB2),
        Color.FromRgb(0x59, 0xA1, 0x4F),
        Color.FromRgb(0xED, 0xC9, 0x48),
        Color.FromRgb(0xB0, 0x7A, 0xA1),
        Color.FromRgb(0xFF, 0x9D, 0xA7),
        Color.FromRgb(0x9C, 0x75, 0x5F),
        Color.FromRgb(0xBA, 0xB0, 0xAC),
    };

    private static readonly Color OtherColor = Color.FromRgb(0xB8, 0xB8, 0xB8);

    public static readonly DependencyProperty RootProperty =
        DependencyProperty.Register(
            nameof(Root),
            typeof(ScanTreeNode),
            typeof(TreemapControl),
            new FrameworkPropertyMetadata(null, (d, _) => ((TreemapControl)d).OnRootChanged()));

    public ScanTreeNode? Root
    {
        get => (ScanTreeNode?)GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    private int _renderedNodes;

    // What is actually rendered. Kept separate from the Root dependency property so that
    // data updates arriving while the mouse hovers the control can be deferred: the user
    // may be navigating a zoomed view, and swapping the tree would yank it away.
    private ScanTreeNode? _currentTree;

    // Pause-on-hover state: while paused, Root changes only set _pendingDataChange and
    // are applied (with zoom reset) when the mouse leaves.
    private bool _isPaused;
    private bool _pendingDataChange;
    private Border? _pauseFrame;
    private TextBlock? _pauseIcon;

    public TreemapControl()
    {
        // Transparent background so the whole area hit-tests and hover-pause also works
        // over the gaps between tiles (null background would let hits fall through).
        Background = Brushes.Transparent;
    }

    // Zoom state: path from Root to the currently displayed node. Empty = full overview.
    private readonly List<ScanTreeNode> _zoomStack = new();

    private ScanTreeNode? ZoomedNode => _zoomStack.Count > 0 ? _zoomStack[_zoomStack.Count - 1] : null;

    /// <summary>Zooms into a directory node so it fills the whole control.</summary>
    public void ZoomIn(ScanTreeNode node)
    {
        if (node.Children.Count == 0) return;
        _zoomStack.Add(node);
        Rebuild();
    }

    /// <summary>Goes one zoom level up; no-op when already at the overview.</summary>
    public void ZoomOut()
    {
        if (_zoomStack.Count == 0) return;
        _zoomStack.RemoveAt(_zoomStack.Count - 1);
        Rebuild();
    }

    private void ResetZoom()
    {
        if (_zoomStack.Count == 0) return;
        _zoomStack.Clear();
        Rebuild();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Rebuild();
    }

    // New data invalidates any zoom — the zoomed node may no longer exist in the new tree.
    // While the mouse hovers the control the update is deferred until it leaves.
    private void OnRootChanged()
    {
        if (_isPaused)
        {
            _pendingDataChange = true;
            return;
        }
        _currentTree = Root;
        _zoomStack.Clear();
        Rebuild();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _isPaused = true;
        ShowPauseOverlay();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _isPaused = false;
        HidePauseOverlay();
        if (_pendingDataChange)
        {
            _pendingDataChange = false;
            _currentTree = Root;
            _zoomStack.Clear();
            Rebuild();
        }
    }

    private void ShowPauseOverlay()
    {
        if (_pauseFrame != null || ActualWidth < 20 || ActualHeight < 20) return;

        _pauseFrame = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
            BorderThickness = new Thickness(3),
            IsHitTestVisible = false,
            Width = ActualWidth,
            Height = ActualHeight,
        };
        _pauseIcon = new TextBlock
        {
            Text = "\u23F8", // ⏸
            FontSize = 26,
            Foreground = Brushes.White,
            Background = _pauseFrame.BorderBrush,
            Padding = new Thickness(8, 4, 8, 4),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(_pauseFrame, 0);
        Canvas.SetTop(_pauseFrame, 0);
        Canvas.SetTop(_pauseIcon, 5);
        Canvas.SetRight(_pauseIcon, 5);
        Children.Add(_pauseFrame);
        Children.Add(_pauseIcon);
    }

    private void HidePauseOverlay()
    {
        if (_pauseFrame != null) Children.Remove(_pauseFrame);
        if (_pauseIcon != null) Children.Remove(_pauseIcon);
        _pauseFrame = null;
        _pauseIcon = null;
    }

    private void Rebuild()
    {
        Children.Clear();
        _pauseFrame = null;
        _pauseIcon = null;
        _renderedNodes = 0;

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 20 || height < 20) return;

        var root = _currentTree;
        if (root == null || root.Children.Count == 0 || root.TotalSeconds <= 0)
        {
            ShowPlaceholder("No file scans recorded yet.\nScan something or open an .etl recording.");
            return;
        }

        var displayRoot = ZoomedNode ?? root;
        RenderChildren(displayRoot, new Rect(0, 0, width, height), depth: 0, parentColor: default);

        if (ZoomedNode != null)
            AddBreadcrumbOverlay(ZoomedNode);

        if (_isPaused)
            ShowPauseOverlay();
    }

    private void RenderChildren(ScanTreeNode parent, Rect area, int depth, Color parentColor)
    {
        if (depth > MaxDepth) return;
        if (area.Width < 4 || area.Height < 4) return;

        var items = SelectVisibleItems(parent);
        if (items.Count == 0) return;

        var weights = new double[items.Count];
        for (int i = 0; i < items.Count; i++) weights[i] = items[i].Weight;

        var rects = Squarify(weights, area);

        for (int i = 0; i < items.Count; i++)
        {
            if (_renderedNodes >= MaxRenderedNodes) return;

            var r = rects[i];
            r = new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2));
            if (r.Width < 3 || r.Height < 3) continue;

            var item = items[i];
            var color = item.Node == null
                ? OtherColor
                : depth == 0 ? Palette[i % Palette.Length] : Lighten(parentColor, 0.12);

            // Directories with room get a header strip and their children nested inside;
            // smaller ones render as solid tiles showing their aggregated total.
            var showHeader = item.Node is { Children.Count: > 0 }
                             && r.Width >= MinHeaderWidth
                             && r.Height >= HeaderHeight + MinHeaderBodyHeight;

            AddNodeVisual(item, r, color, showHeader);
            _renderedNodes++;

            if (showHeader)
            {
                var content = new Rect(
                    r.X + 2,
                    r.Y + HeaderHeight,
                    Math.Max(0, r.Width - 4),
                    Math.Max(0, r.Height - HeaderHeight - 2));
                RenderChildren(item.Node!, content, depth + 1, color);
            }
        }
    }

    /// <summary>
    /// Takes the node's biggest children and folds the long tail into one gray "Other" tile,
    /// so the treemap stays readable and element count stays bounded.
    /// </summary>
    private List<Item> SelectVisibleItems(ScanTreeNode parent)
    {
        var minWeight = (ZoomedNode ?? _currentTree!).TotalSeconds * MinNodeFraction;
        var items = new List<Item>();
        double otherWeight = 0;
        var otherCount = 0;

        foreach (var child in parent.Children) // already sorted by TotalSeconds desc
        {
            if (items.Count < MaxChildrenPerNode && child.TotalSeconds >= minWeight)
            {
                items.Add(new Item(child, child.TotalSeconds));
            }
            else
            {
                otherWeight += child.TotalSeconds;
                otherCount++;
            }
        }

        if (otherCount > 0)
            items.Add(new Item(null, otherWeight) { OtherCount = otherCount });

        return items;
    }

    private void AddNodeVisual(Item item, Rect r, Color color, bool showHeader)
    {
        var name = item.Node?.Name ?? $"Other ({item.OtherCount})";

        Border border;
        if (showHeader)
        {
            border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B)),
                BorderBrush = new SolidColorBrush(color),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = name,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Darken(color, 0.45)),
                    Margin = new Thickness(3, 1, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Top,
                },
            };
        }
        else
        {
            var fill = item.Node?.Children.Count > 0 ? Lighten(color, 0.1) : color;
            border = new Border
            {
                Background = new SolidColorBrush(fill),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = name,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Luminance(fill) > 0.6 ? Colors.Black : Colors.White),
                    Margin = new Thickness(3, 1, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }

        border.Width = r.Width;
        border.Height = r.Height;
        border.ToolTip = BuildToolTip(item);

        // Hover feedback: brighten the tile and thicken its frame.
        var normalBackground = border.Background;
        var normalThickness = border.BorderThickness;
        var hoverBackground = Brighten((SolidColorBrush)normalBackground);
        border.MouseEnter += (_, __) =>
        {
            border.Background = hoverBackground;
            border.BorderThickness = new Thickness(2);
        };
        border.MouseLeave += (_, __) =>
        {
            border.Background = normalBackground;
            border.BorderThickness = normalThickness;
        };

        if (item.Node is { } node)
        {
            border.ContextMenu = BuildContextMenu(node);
            if (node.Children.Count > 0)
            {
                border.Cursor = Cursors.Hand;
                border.MouseLeftButtonDown += (_, e) =>
                {
                    if (e.ClickCount == 2)
                    {
                        ZoomIn(node);
                        e.Handled = true;
                    }
                };
            }
        }

        Canvas.SetLeft(border, r.X);
        Canvas.SetTop(border, r.Y);
        Children.Add(border);
    }

    /// <summary>
    /// Small overlay shown while zoomed in: displays the current path and jumps back
    /// to the full overview when clicked.
    /// </summary>
    private void AddBreadcrumbOverlay(ScanTreeNode zoomed)
    {
        var button = new Button
        {
            Content = $"\u2190 {zoomed.FullPath}  (back to overview)",
            Padding = new Thickness(6, 2, 6, 2),
            FontSize = 11,
            ToolTip = "Click to reset zoom (or right-click a tile \u2192 Zoom Out)",
        };
        button.Click += (_, __) => ResetZoom();
        button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        button.Width = Math.Min(button.DesiredSize.Width, Math.Max(0, ActualWidth - 8));
        Canvas.SetLeft(button, 4);
        Canvas.SetTop(button, 4);
        Children.Add(button);
    }

    // --- Tile actions ---

    private ContextMenu BuildContextMenu(ScanTreeNode node)
    {
        var menu = new ContextMenu();

        menu.Items.Add(new MenuItem
        {
            Header = "Copy Path",
            Icon = new TextBlock { Text = "\U0001F4CB" },
            Command = new RelayCommand(() => Clipboard.SetText(node.FullPath)),
        });

        menu.Items.Add(new MenuItem
        {
            Header = "Open in Explorer",
            Icon = new TextBlock { Text = "\U0001F4C1" },
            Command = new RelayCommand(() => OpenInExplorer(node)),
        });

        menu.Items.Add(new MenuItem
        {
            Header = "Add to Defender Exclusion\u2026",
            Icon = new TextBlock { Text = "\U0001F6E1" },
            Command = new RelayCommand(() => DefenderExclusions.AddPathExclusion(node.FullPath)),
        });

        menu.Items.Add(new Separator());

        menu.Items.Add(new MenuItem
        {
            Header = "Zoom In",
            InputGestureText = "Double-click",
            Command = new RelayCommand(() => ZoomIn(node)),
            IsEnabled = node.Children.Count > 0,
        });

        menu.Items.Add(new MenuItem
        {
            Header = "Zoom Out",
            Command = new RelayCommand(ZoomOut),
            IsEnabled = ZoomedNode != null,
        });

        return menu;
    }

    private static void OpenInExplorer(ScanTreeNode node)
    {
        try
        {
            // Directories open directly; files are shown selected in their parent folder.
            var arguments = node.Children.Count > 0
                ? $"\"{node.FullPath}\""
                : $"/select,\"{node.FullPath}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open Explorer for:\n{node.FullPath}\n\n{ex.Message}",
                "Open in Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Minimal ICommand so menu items stay declarative without pulling in the ViewModel.
    private sealed class RelayCommand : ICommand
    {
        private readonly Action _action;
        public RelayCommand(Action action) => _action = action;
#pragma warning disable CS0067 // CanExecuteChanged is never used — required by ICommand
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _action();
    }

    private string BuildToolTip(Item item)
    {
        var total = _currentTree?.TotalSeconds ?? 0;
        var pct = total > 0 ? item.Weight / total * 100 : 0;
        if (item.Node is not { } node)
            return $"{item.OtherCount} smaller paths\n{item.Weight:F2}s ({pct:F1}%)";
        var zoomHint = node.Children.Count > 0
            ? "\nDouble-click to zoom in \u00b7 right-click for more actions"
            : "\nRight-click for actions";
        return $"{node.FullPath}\n{node.TotalSeconds:F2}s ({pct:F1}%){zoomHint}";
    }

    private void ShowPlaceholder(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = Brushes.Gray,
            TextAlignment = TextAlignment.Center,
        };
        tb.Measure(new Size(ActualWidth, ActualHeight));
        Canvas.SetLeft(tb, Math.Max(0, (ActualWidth - tb.DesiredSize.Width) / 2));
        Canvas.SetTop(tb, Math.Max(0, (ActualHeight - tb.DesiredSize.Height) / 2));
        Children.Add(tb);
    }

    // --- Squarified treemap layout (Bruls, Huizing, van Wijk) ---

    internal static Rect[] Squarify(IReadOnlyList<double> weights, Rect bounds)
    {
        var result = new Rect[weights.Count];
        double total = 0;
        for (int i = 0; i < weights.Count; i++) total += weights[i];
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;

        var scale = bounds.Width * bounds.Height / total;
        var areas = new double[weights.Count];
        for (int i = 0; i < weights.Count; i++) areas[i] = weights[i] * scale;

        var remaining = bounds;
        var start = 0;
        while (start < areas.Length)
        {
            var side = Math.Min(remaining.Width, remaining.Height);
            if (side <= 0) break;

            // Grow the current row while the worst aspect ratio improves.
            var end = start;
            double rowArea = 0;
            var worst = double.MaxValue;
            while (end < areas.Length)
            {
                var newRowArea = rowArea + areas[end];
                var newWorst = WorstRatio(areas, start, end, newRowArea, side);
                if (end > start && newWorst > worst) break;
                worst = newWorst;
                rowArea = newRowArea;
                end++;
            }

            var thickness = rowArea / side;
            if (remaining.Width >= remaining.Height)
            {
                // Vertical strip on the left, items stacked top to bottom.
                var stripW = Math.Min(thickness, remaining.Width);
                var y = remaining.Y;
                for (var k = start; k < end; k++)
                {
                    var itemH = Math.Max(0, Math.Min(areas[k] / thickness, remaining.Bottom - y));
                    result[k] = new Rect(remaining.X, y, stripW, itemH);
                    y += itemH;
                }
                remaining = new Rect(remaining.X + stripW, remaining.Y,
                    Math.Max(0, remaining.Width - stripW), remaining.Height);
            }
            else
            {
                // Horizontal strip on top, items laid left to right.
                var stripH = Math.Min(thickness, remaining.Height);
                var x = remaining.X;
                for (var k = start; k < end; k++)
                {
                    var itemW = Math.Max(0, Math.Min(areas[k] / thickness, remaining.Right - x));
                    result[k] = new Rect(x, remaining.Y, itemW, stripH);
                    x += itemW;
                }
                remaining = new Rect(remaining.X, remaining.Y + stripH,
                    remaining.Width, Math.Max(0, remaining.Height - stripH));
            }
            start = end;
        }
        return result;
    }

    internal static double WorstRatio(double[] areas, int start, int end, double rowArea, double side)
    {
        var thickness = rowArea / side;
        var worst = 0.0;
        for (var k = start; k <= end; k++)
        {
            var length = areas[k] / thickness;
            if (length <= 0) return double.MaxValue;
            var ratio = Math.Max(thickness / length, length / thickness);
            if (ratio > worst) worst = ratio;
        }
        return worst;
    }

    // --- Color helpers ---

    // Lightens a brush's color while preserving its alpha (header tiles use translucent fills).
    private static SolidColorBrush Brighten(SolidColorBrush brush)
    {
        var c = brush.Color;
        var lighter = Lighten(c, 0.22);
        return new SolidColorBrush(Color.FromArgb(c.A, lighter.R, lighter.G, lighter.B));
    }

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    private static Color Lighten(Color c, double t) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * t),
        (byte)(c.G + (255 - c.G) * t),
        (byte)(c.B + (255 - c.B) * t));

    private static Color Darken(Color c, double t) => Color.FromRgb(
        (byte)(c.R * (1 - t)),
        (byte)(c.G * (1 - t)),
        (byte)(c.B * (1 - t)));

    private sealed class Item
    {
        public readonly ScanTreeNode? Node; // null = aggregated "Other" tile
        public readonly double Weight;
        public int OtherCount;

        public Item(ScanTreeNode? node, double weight)
        {
            Node = node;
            Weight = weight;
        }
    }
}
