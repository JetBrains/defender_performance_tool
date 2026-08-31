using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DefenderPerformanceTool;

public partial class ExclusionManagerWindow : Window
{
    public ExclusionManagerViewModel ViewModel { get; }

    public ExclusionManagerWindow(ExclusionManagerViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }
}

/// <summary>Red brush for error status messages, dark green otherwise.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0x00, 0x00));
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x20, 0x60, 0x20));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? ErrorBrush : OkBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
