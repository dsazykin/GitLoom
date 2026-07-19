using System;
using Avalonia;
using Avalonia.Controls;

namespace GitLoom.App.Views;

public partial class CloneDashboardView : UserControl
{
    public static readonly StyledProperty<double> CardWidthProperty =
        AvaloniaProperty.Register<CloneDashboardView, double>(nameof(CardWidth), 300.0);

    public double CardWidth
    {
        get => GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    public CloneDashboardView()
    {
        InitializeComponent();
        this.SizeChanged += CloneDashboardView_SizeChanged;
    }

    private void CloneDashboardView_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        double availableWidth = e.NewSize.Width - 40 - 20; // 40 for ScrollViewer padding, 20 for scrollbar safety
        if (availableWidth <= 0) return;

        double minCardWidth = 300.0;
        double cardSpace = minCardWidth + 20.0; // Margin="10" is 20px horizontal

        int columns = (int)(availableWidth / cardSpace);
        if (columns < 1) columns = 1;

        double totalMargins = columns * 20.0;
        double cardWidth = (availableWidth - totalMargins) / columns;

        CardWidth = Math.Floor(cardWidth);
    }
}
