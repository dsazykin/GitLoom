using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GitLoom.App.Services;

namespace GitLoom.App.Views;

public partial class DeviceFlowAuthDialog : ChromedWindow
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

        BrowserLauncher.OpenUrl(UrlText.Text);
    }

    private void Url_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        BrowserLauncher.OpenUrl(UrlText.Text);
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
}
