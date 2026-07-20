using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Styling;
using Avalonia.Markup.Xaml;
using Avalonia.Headless.XUnit;
using Xunit;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Mainguard.StyleTests.TestApp))]

namespace Mainguard.StyleTests
{
    public class TestApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        }
    }

    public class StyleResolutionTests
    {
        [AvaloniaFact]
        public void ToggleButton_Checked_Style_Should_Apply_To_PathIcon()
        {
            // Arrange
            var window = new Window();
            
            // XAML-like structure
            var tb = new ToggleButton();
            tb.Classes.Add("StagingCategory");
            
            var stackPanel = new StackPanel();
            var pathIcon = new PathIcon();
            pathIcon.Classes.Add("Chevron");
            
            stackPanel.Children.Add(pathIcon);
            tb.Content = stackPanel;
            window.Content = tb;

            // Apply style mimicking StagingPanelView
            var style = new Style(x => x.OfType<ToggleButton>().Class("StagingCategory").Class(":checked").Descendant().OfType<PathIcon>().Class("Chevron"));
            style.Setters.Add(new Setter(PathIcon.WidthProperty, 99.0)); // Test property
            
            window.Styles.Add(style);

            // Act
            window.Show();
            tb.IsChecked = true;

            // Assert
            Assert.Equal(99.0, pathIcon.Width);
        }
    }
}
