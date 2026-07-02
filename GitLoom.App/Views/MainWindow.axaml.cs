using System;
using System.Linq;
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

        if (DataContext is MainWindowViewModel vm && sender is Control control && control.DataContext != null)
        {
            vm.SelectedNode = control.DataContext;
        }
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

    private void Category_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStartPoint = e.GetPosition(this);

        if (DataContext is MainWindowViewModel vm && sender is Control control && control.DataContext != null)
        {
            vm.SelectedNode = control.DataContext;
        }
    }

    private void Category_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is Control control && control.DataContext != null)
        {
            vm.SelectedNode = control.DataContext;
        }
    }

    private async void Category_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !_isDragging)
        {
            var point = e.GetPosition(this);
            if (Math.Abs(point.X - _dragStartPoint.X) > 3 || Math.Abs(point.Y - _dragStartPoint.Y) > 3)
            {
                _isDragging = true;
                if (sender is Control control && control.DataContext is WorkspaceCategory cat)
                {
                    var data = new DataObject();
                    data.Set("Category", cat);
                    await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
                }
                _isDragging = false;
            }
        }
    }

    private void Category_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            if (e.Source is Control control)
            {
                if (e.Data.Get("Repository") is Repository droppedRepo)
                {
                    if (control.DataContext is WorkspaceCategory targetCategory)
                    {
                        vm.MoveRepositoryToCategory(droppedRepo, targetCategory);
                    }
                    else if (control.DataContext is Repository targetRepo)
                    {
                        var targetCat = vm.Categories.FirstOrDefault(c => c.CategoryId == targetRepo.CategoryId) ??
                                        vm.Categories.SelectMany(c => c.SubCategories).FirstOrDefault(c => c.CategoryId == targetRepo.CategoryId);
                        if (targetCat != null)
                        {
                            vm.MoveRepositoryToCategory(droppedRepo, targetCat);
                        }
                    }
                }
                else if (e.Data.Get("Category") is WorkspaceCategory droppedCategory)
                {
                    if (control.DataContext is WorkspaceCategory targetCategory)
                    {
                        vm.MoveCategoryToCategory(droppedCategory, targetCategory);
                    }
                    else if (control.DataContext is Repository targetRepo)
                    {
                        var targetCat = vm.Categories.FirstOrDefault(c => c.CategoryId == targetRepo.CategoryId) ??
                                        vm.Categories.SelectMany(c => c.SubCategories).FirstOrDefault(c => c.CategoryId == targetRepo.CategoryId);
                        if (targetCat != null)
                        {
                            vm.MoveCategoryToCategory(droppedCategory, targetCat);
                        }
                    }
                    else if (control.DataContext is MainWindowViewModel || control is ScrollViewer || control is ItemsControl || control.Name == "SidebarRoot")
                    {
                        // Dropped on the background, un-nest it
                        vm.MoveCategoryToCategory(droppedCategory, null);
                    }
                }
            }
        }
    }

    public void CategoryNameTextBox_KeyUp(object? sender, KeyEventArgs e)
    {
        if (sender is TextBox textBox && textBox.DataContext is WorkspaceCategory cat)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                if (e.Key == Key.Enter)
                {
                    vm.SaveCategoryNameCommand.Execute(cat);
                }
                else if (e.Key == Key.Escape)
                {
                    vm.CancelCategoryNameCommand.Execute(cat);
                }
            }
        }
    }

    public void CategoryNameTextBox_AttachedToVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            // Focus and select the text immediately when attached (which happens when IsEditingName becomes true and it becomes visible)
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private void SidebarBackground_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            if (e.Source is Control control && !(control.DataContext is WorkspaceCategory) && !(control.DataContext is Repository))
            {
                vm.SelectedNode = null;
            }
        }
    }

    private void Window_KeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            if (vm.IsDeleteConfirmationVisible)
            {
                if (e.Key == Key.Enter)
                {
                    vm.ConfirmDeleteCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.Escape)
                {
                    vm.CancelDeleteCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Delete)
            {
                if (vm.SelectedNode is Repository repo)
                {
                    vm.RemoveRepositoryCommand.Execute(repo);
                }
                else if (vm.SelectedNode is WorkspaceCategory cat)
                {
                    vm.DeleteCategoryCommand.Execute(cat);
                }
            }
            else if (e.Key == Key.F2)
            {
                if (vm.SelectedNode is WorkspaceCategory cat)
                {
                    vm.RenameCategoryCommand.Execute(cat);
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
                vm.IsCommandPaletteOpen = false;
            }
        }
    }

    private void CommandPaletteBackground_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.IsCommandPaletteOpen = false;
        }
    }

    private void CommandPaletteWindow_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        // Stop the click inside the palette from bubbling up to the background and closing the window
        e.Handled = true;
    }
}
