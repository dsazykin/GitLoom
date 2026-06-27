using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Styling;
using Avalonia.Markup.Xaml;
using Avalonia.Themes.Fluent;
using Avalonia.Media;
using Avalonia.Animation;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GitLoom.App.Views;
using Avalonia.VisualTree;

namespace GitLoom.StyleConsole
{
    class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            BuildAvaloniaApp().Start(AppMain, args);
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<GitLoom.App.App>()
                .UsePlatformDetect()
                .LogToTrace();

        static void AppMain(Application app, string[] args)
        {
            var window = new Window();

            var stagingPanel = new StagingPanelView();
            window.Content = stagingPanel;

            window.Show();

            DispatcherTimer.RunOnce(() =>
            {
                var toggleButton = stagingPanel.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(t => t.Classes.Contains("StagingCategory"));
                if (toggleButton == null) {
                    Console.WriteLine("ToggleButton NOT FOUND");
                    Environment.Exit(1);
                    return;
                }
                
                var pathIcon = toggleButton.GetVisualDescendants().OfType<PathIcon>().FirstOrDefault(p => p.Classes.Contains("Chevron"));
                if (pathIcon == null) {
                    Console.WriteLine("PathIcon NOT FOUND");
                    Environment.Exit(1);
                    return;
                }

                var rotateTransform = pathIcon.RenderTransform as RotateTransform;
                Console.WriteLine("Initial Angle: " + (rotateTransform?.Angle.ToString() ?? "NULL"));
                Console.WriteLine("Initial Classes: " + string.Join(", ", pathIcon.Classes));

                // Force toggle
                toggleButton.IsChecked = true;
                
                // Directly set the class to simulate the binding since we don't have a VM in this headless test attached properly to two-way bindings.
                pathIcon.Classes.Add("expanded");

                DispatcherTimer.RunOnce(() =>
                {
                    Console.WriteLine("After Checked Classes: " + string.Join(", ", pathIcon.Classes));
                    var newRotateTransform = pathIcon.RenderTransform as RotateTransform;
                    Console.WriteLine("After Checked Angle: " + (newRotateTransform?.Angle.ToString() ?? "NULL"));
                    Environment.Exit(0);
                }, TimeSpan.FromMilliseconds(500));
                
            }, TimeSpan.FromMilliseconds(500));

            app.Run(window);
        }
    }
}
