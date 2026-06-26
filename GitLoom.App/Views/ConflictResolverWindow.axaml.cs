using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using System.Linq;
using Avalonia.VisualTree;
using System;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class ConflictResolverWindow : Window
{
    private bool _firstLoadScrolled = false;
    private double _lastExtentHeight = -1;
    private double _lastViewportHeight = -1;
    private double _lastCanvasHeight = -1;

    public ConflictResolverWindow()
    {
        InitializeComponent();
        this.LayoutUpdated += OnLayoutUpdated;
        this.Opened += (s, e) => 
        {
            if (DataContext is ConflictResolverWindowViewModel vm)
            {
                vm.RequestNextConflict += () => ScrollToNextConflict(1);
                vm.RequestPrevConflict += () => ScrollToNextConflict(-1);
            }
        };
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (MainScroll == null || MinimapCanvas == null) return;

        bool needsUpdate = false;
        if (MainScroll.Extent.Height != _lastExtentHeight)
        {
            _lastExtentHeight = MainScroll.Extent.Height;
            needsUpdate = true;
        }
        
        if (MainScroll.Viewport.Height != _lastViewportHeight)
        {
            _lastViewportHeight = MainScroll.Viewport.Height;
            needsUpdate = true;
        }

        if (MinimapCanvas.Bounds.Height != _lastCanvasHeight)
        {
            _lastCanvasHeight = MinimapCanvas.Bounds.Height;
            needsUpdate = true;
        }
        
        if (needsUpdate)
        {
            UpdateMinimap();
        }
        
        if (!_firstLoadScrolled && MainScroll.Extent.Height > 0)
        {
            _firstLoadScrolled = true;
            ScrollToNextConflict(1, true); // start at first
        }
    }

    private void UpdateMinimap()
    {
        var scrollViewer = MainScroll;
        var canvas = MinimapCanvas;
        if (scrollViewer == null || canvas == null) return;
        
        canvas.Children.Clear();
        
        var extentHeight = scrollViewer.Extent.Height;
        var viewportHeight = scrollViewer.Viewport.Height;
        if (extentHeight == 0) return;
        
        var canvasHeight = canvas.Bounds.Height;
        if (canvasHeight == 0) return;
        
        var itemsControl = BlocksControl;
        if (itemsControl == null) return;

        var containers = itemsControl.GetRealizedContainers().ToList();
        
        double currentY = 0;
        foreach (var container in containers)
        {
            if (container is Control c && c.DataContext is ConflictBlockViewModel block && block.IsConflict)
            {
                double yPct = currentY / extentHeight;
                double drawY = yPct * canvasHeight;
                
                double itemHeight = c.Bounds.Height;
                double heightPct = itemHeight / extentHeight;
                double drawHeight = Math.Max(2, heightPct * canvasHeight);
                
                var rect = new Rectangle
                {
                    Fill = new SolidColorBrush(Color.Parse("#F44336")),
                    Width = canvas.Bounds.Width,
                    Height = drawHeight
                };
                Canvas.SetTop(rect, drawY);
                canvas.Children.Add(rect);
            }
            if (container is Control ctrl) currentY += ctrl.Bounds.Height;
        }
    }

    private void ScrollToNextConflict(int direction, bool fromStart = false)
    {
        var scrollViewer = MainScroll;
        var itemsControl = BlocksControl;
        if (scrollViewer == null || itemsControl == null) return;

        var containers = itemsControl.GetRealizedContainers().ToList();
        double currentY = scrollViewer.Offset.Y;
        
        Control? targetContainer = null;
        
        double targetY = 0;
        if (fromStart)
        {
            foreach (var container in containers)
            {
                if (container is Control c)
                {
                    if (c.DataContext is ConflictBlockViewModel b && b.IsConflict)
                    {
                        targetContainer = c;
                        break;
                    }
                    targetY += c.Bounds.Height;
                }
            }
        }
        else
        {
            if (direction > 0)
            {
                foreach (var container in containers)
                {
                    if (container is Control c)
                    {
                        if (c.DataContext is ConflictBlockViewModel b && b.IsConflict && targetY > currentY + 10)
                        {
                            targetContainer = c;
                            break;
                        }
                        targetY += c.Bounds.Height;
                    }
                }
            }
            else
            {
                double tempY = 0;
                foreach (var container in containers)
                {
                    if (container is Control c)
                    {
                        if (c.DataContext is ConflictBlockViewModel b && b.IsConflict && tempY < currentY - 10)
                        {
                            targetContainer = c;
                            targetY = tempY;
                        }
                        tempY += c.Bounds.Height;
                    }
                }
            }
        }

        if (targetContainer != null)
        {
            scrollViewer.Offset = new Avalonia.Vector(scrollViewer.Offset.X, targetY);
        }
    }
}
