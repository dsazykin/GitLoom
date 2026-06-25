using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GitLoom.App.Views
{
    public partial class DeviceFlowAuthDialog : Window
    {
        public DeviceFlowAuthDialog()
        {
            InitializeComponent();
        }

        public DeviceFlowAuthDialog(string url, string code) : this()
        {
            UrlText.Text = url;
            CodeText.Text = code;
            this.Opened += DeviceFlowAuthDialog_Opened;
        }

        private async void DeviceFlowAuthDialog_Opened(object? sender, EventArgs e)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null && CodeText.Text != null)
            {
                await clipboard.SetTextAsync(CodeText.Text);
            }
            
            OpenBrowser(UrlText.Text);
        }

        private void Url_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            OpenBrowser(UrlText.Text);
        }

        private async void CopyCode_Click(object? sender, RoutedEventArgs e)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null && CodeText.Text != null)
            {
                await clipboard.SetTextAsync(CodeText.Text);
            }
        }

        private void Cancel_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OpenBrowser(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            try
            {
                Process.Start(url);
            }
            catch
            {
                // hack because of this: https://github.com/dotnet/corefx/issues/10361
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    url = url.Replace("&", "^&");
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Process.Start("xdg-open", url);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", url);
                }
            }
        }
    }
}
