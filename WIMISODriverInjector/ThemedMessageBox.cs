using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WIMISODriverInjector
{
    /// <summary>
    /// Shows a message dialog that uses the application theme (dark/light) so it doesn't appear as blinding white in dark mode.
    /// </summary>
    public static class ThemedMessageBox
    {
        public static void Show(Window? owner, string message, string title, bool isError = false)
        {
            var app = Application.Current;
            var background = (Brush)(app.FindResource("BackgroundBrush") ?? new SolidColorBrush(Colors.White));
            var foreground = (Brush)(app.FindResource("PrimaryTextBrush") ?? new SolidColorBrush(Colors.Black));

            var window = new Window
            {
                Title = title,
                Background = background,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Owner = owner,
                WindowStyle = WindowStyle.ToolWindow,
                MinWidth = 320,
                MaxWidth = 560
            };

            var grid = new Grid { Margin = new Thickness(24, 20, 24, 20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var textBlock = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = foreground,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 0)
            };
            Grid.SetRow(textBlock, 0);

            var button = new Button
            {
                Content = "OK",
                IsDefault = true,
                IsCancel = true,
                HorizontalAlignment = HorizontalAlignment.Right,
                MinWidth = 80,
                Padding = new Thickness(16, 8, 16, 8)
            };
            try
            {
                var buttonStyle = app.FindResource("ButtonStyle");
                if (buttonStyle is Style s)
                    button.Style = s;
            }
            catch { }
            button.Click += (_, __) => window.DialogResult = true;
            Grid.SetRow(button, 2);

            grid.Children.Add(textBlock);
            grid.Children.Add(button);

            window.Content = grid;
            window.Loaded += (_, __) => button.Focus();

            window.ShowDialog();
        }
    }
}
