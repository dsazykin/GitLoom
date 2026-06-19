using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using GitLoom.App.ViewModels;
using GitLoom.Core.Models;

namespace GitLoom.App.Views;

public partial class MainWindow : Window
{
    private Point _dragStartPoint;
    private bool _isDragging;

    public MainWindow()
    {
        InitializeComponent();

        // Globally listen for Drops anywhere in the Window
        AddHandler(DragDrop.DropEvent, Category_Drop);
    }

    private void Repo_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Record where the mouse was first clicked
        _dragStartPoint = e.GetPosition(this);
    }

    private async void Repo_PointerMoved(object? sender, PointerEventArgs e)
    {
        // If they are holding the left mouse button, and they moved the mouse more than 3 pixels, start the drag!
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !_isDragging)
        {
            var point = e.GetPosition(this);
            if (Math.Abs(point.X - _dragStartPoint.X) > 3 || Math.Abs(point.Y - _dragStartPoint.Y) > 3)
            {
                _isDragging = true;

                if (sender is Control control && control.DataContext is Repository repo)
                {
                    // Package the Repository up into a Drag payload
                    var data = new DataObject();
                    data.Set("Repository", repo);

                    // Begin the native visual drag!
                    await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
                }

                _isDragging = false;
            }
        }
    }

    private void Category_Drop(object? sender, DragEventArgs e)
    {
        // e.Source tells us the exact element (like the Grid or TextBlock) the mouse dropped onto!
        if (e.Data.Get("Repository") is Repository droppedRepo)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                if (e.Source is Control control)
                {
                    if (control.DataContext is WorkspaceCategory targetCategory)
                    {
                        vm.MoveRepositoryToCategory(droppedRepo, targetCategory);
                    }
                    else if (control.DataContext is Repository targetRepo)
                    {
                        // User dropped it on another repo. Find its parent category.
                        var targetCat = vm.Categories.FirstOrDefault(c => c.CategoryId == targetRepo.CategoryId);
                        if (targetCat != null)
                        {
                            vm.MoveRepositoryToCategory(droppedRepo, targetCat);
                        }
                    }
                }
            }
        }
    }

    private void Repo_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control control && control.DataContext is Repository repo)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.OpenRepository(repo);
            }
        }
    }
}