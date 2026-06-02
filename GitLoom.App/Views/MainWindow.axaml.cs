using Avalonia.Controls;
using Avalonia.Input;

namespace GitLoom.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // This makes the entire window movable when clicking any empty space!
        this.PointerPressed += (sender, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };
    }
}