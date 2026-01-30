using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WIMISODriverInjector;

/// <summary>
/// Shows a message dialog using WinUI ContentDialog, themed to match the app.
/// </summary>
public static class ThemedMessageBox
{
    public static async Task ShowAsync(Window? owner, string message, string title, bool isError = false)
    {
        var xamlRoot = (owner?.Content as Microsoft.UI.Xaml.FrameworkElement)?.XamlRoot;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                FontSize = 14,
                Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 0)
            },
            PrimaryButtonText = "OK",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        try
        {
            var app = Microsoft.UI.Xaml.Application.Current as App;
            if (app?.Resources != null)
            {
                try
                {
                    if (app.Resources["BackgroundBrush"] is SolidColorBrush bgBrush)
                        dialog.Background = bgBrush;
                }
                catch { }
                try
                {
                    if (app.Resources["PrimaryTextBrush"] is SolidColorBrush fgBrush)
                        dialog.Foreground = fgBrush;
                }
                catch { }
            }
        }
        catch { }

        await dialog.ShowAsync();
    }

    /// <summary>
    /// Synchronous show for callers that cannot await. Fire-and-forget from UI thread.
    /// </summary>
    public static void Show(Window? owner, string message, string title, bool isError = false)
    {
        _ = ShowAsync(owner, message, title, isError);
    }
}
