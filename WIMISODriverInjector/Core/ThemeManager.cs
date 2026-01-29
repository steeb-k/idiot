using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WIMISODriverInjector.Core
{
    public class ThemeManager
    {
        private RegistryKeyWatcher? _registryWatcher;
        private bool _isDarkMode;

        public event EventHandler<bool>? ThemeChanged;

        public bool IsDarkMode => _isDarkMode;

        public ThemeManager()
        {
            _isDarkMode = DetectSystemTheme();
            System.Diagnostics.Debug.WriteLine($"ThemeManager initialized. Dark mode: {_isDarkMode}");
            
            // Write to file for debugging - use same location as startup-log.txt
            // Force create the file first, then append
            try
            {
                var debugPath = "theme-debug.txt";
                if (!File.Exists(debugPath))
                {
                    File.WriteAllText(debugPath, ""); // Create empty file
                }
                File.AppendAllText(debugPath, 
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ThemeManager created. Dark mode: {_isDarkMode}\n");
                System.Diagnostics.Debug.WriteLine($"Theme debug file: {debugPath} (current directory: {Environment.CurrentDirectory})");
            }
            catch (Exception ex)
            {
                // Write error to startup-log.txt so user can see it
                try
                {
                    File.AppendAllText("startup-log.txt", 
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: Failed to write theme-debug.txt: {ex.Message}\n");
                }
                catch { }
                System.Diagnostics.Debug.WriteLine($"Failed to write theme debug: {ex.Message}");
            }
        }

        public void Initialize(Application app)
        {
            System.Diagnostics.Debug.WriteLine($"Initializing theme manager. Current theme: {(_isDarkMode ? "Dark" : "Light")}");
            
            try
            {
                var exeDir = AppContext.BaseDirectory;
                var debugPath = System.IO.Path.Combine(exeDir, "theme-debug.txt");
                File.AppendAllText(debugPath, 
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Initialize() called. IsDarkMode: {_isDarkMode}\n");
            }
            catch { }
            
            // Apply initial theme
            ApplyTheme(app, _isDarkMode);
        }

        public void WatchForChanges(Application app)
        {
            // Watch for theme changes via registry
            WatchRegistryTheme(app);
        }

        // Make CreateThemeDictionary public so it can be called from App.xaml.cs
        public ResourceDictionary CreateThemeDictionary(bool isDark)
        {
            return CreateThemeDictionaryInternal(isDark);
        }

        private bool DetectSystemTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key != null)
                {
                    var appsUseLightTheme = key.GetValue("AppsUseLightTheme");
                    if (appsUseLightTheme is int value)
                    {
                        var isDark = value == 0; // 0 = dark mode, 1 = light mode
                        System.Diagnostics.Debug.WriteLine($"Detected system theme: {(isDark ? "Dark" : "Light")} (registry value: {value})");
                        return isDark;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Theme detection error: {ex.Message}");
            }

            // Default to light mode if detection fails
            System.Diagnostics.Debug.WriteLine("Theme detection failed, defaulting to Light mode");
            return false;
        }

        private void WatchRegistryTheme(Application app)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
                
                if (key != null)
                {
                    // Monitor registry changes
                    _registryWatcher = new RegistryKeyWatcher(key, "AppsUseLightTheme");
                    _registryWatcher.ValueChanged += (sender, e) =>
                    {
                        var newTheme = DetectSystemTheme();
                        if (newTheme != _isDarkMode)
                        {
                            _isDarkMode = newTheme;
                            app.Dispatcher.Invoke(() =>
                            {
                                ApplyTheme(app, _isDarkMode);
                                ThemeChanged?.Invoke(this, _isDarkMode);
                            });
                        }
                    };
                }
            }
            catch
            {
                // If registry watching fails, just use initial detection
            }
        }

        private void ApplyTheme(Application app, bool isDark)
        {
            try
            {
                // Write debug before dispatcher invoke
                try
                {
                    File.AppendAllText("theme-debug.txt", 
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ApplyTheme() called. isDark: {isDark}\n");
                }
                catch { }
                
                app.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var exeDir = AppContext.BaseDirectory;
                        var debugPath = System.IO.Path.Combine(exeDir, "theme-debug.txt");
                        File.AppendAllText(debugPath, 
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Inside Dispatcher.Invoke\n");
                    }
                    catch { }
                    
                    var resources = app.Resources;
                    if (resources == null)
                    {
                        System.Diagnostics.Debug.WriteLine("App.Resources is null!");
                        try
                        {
                            File.AppendAllText("theme-debug.txt", 
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: App.Resources is null!\n");
                        }
                        catch { }
                        return;
                    }

                    var mergedDictionaries = resources.MergedDictionaries;
                    if (mergedDictionaries == null)
                    {
                        System.Diagnostics.Debug.WriteLine("MergedDictionaries is null!");
                        try
                        {
                            var exeDir = AppContext.BaseDirectory;
                            var debugPath = System.IO.Path.Combine(exeDir, "theme-debug.txt");
                            File.AppendAllText(debugPath, 
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: MergedDictionaries is null!\n");
                        }
                        catch { }
                        return;
                    }

                    System.Diagnostics.Debug.WriteLine($"Applying theme: {(isDark ? "Dark" : "Light")}");
                    System.Diagnostics.Debug.WriteLine($"Current merged dictionaries count: {mergedDictionaries.Count}");

                    // Remove existing theme dictionaries
                    int removedCount = 0;
                    for (int i = mergedDictionaries.Count - 1; i >= 0; i--)
                    {
                        var dict = mergedDictionaries[i];
                        bool shouldRemove = false;
                        
                        // Check by source URI
                        if (dict.Source != null)
                        {
                            var sourceStr = dict.Source.OriginalString;
                            if (sourceStr.Contains("LightTheme") || 
                                sourceStr.Contains("DarkTheme") ||
                                sourceStr.Contains("Themes"))
                            {
                                shouldRemove = true;
                            }
                        }
                        // Check if it's a programmatically created theme dict
                        else if (dict.Contains("ThemeBrush"))
                        {
                            shouldRemove = true;
                        }
                        
                        if (shouldRemove)
                        {
                            mergedDictionaries.RemoveAt(i);
                            removedCount++;
                        }
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"Removed {removedCount} existing theme dictionary(ies)");

                    // Create theme dictionary programmatically (works in single-file mode)
                    var themeDict = CreateThemeDictionaryInternal(isDark);
                    mergedDictionaries.Add(themeDict);
                    
                    System.Diagnostics.Debug.WriteLine($"Theme applied: {(isDark ? "Dark" : "Light")}");
                    System.Diagnostics.Debug.WriteLine($"Total merged dictionaries: {mergedDictionaries.Count}");
                    
                    // Verify the brushes are actually in the dictionary
                    if (themeDict.Contains("BackgroundBrush"))
                    {
                        var bgBrush = themeDict["BackgroundBrush"] as SolidColorBrush;
                        System.Diagnostics.Debug.WriteLine($"BackgroundBrush color: {bgBrush?.Color}");
                        
                        // Write to file for debugging
                        try
                        {
                            File.AppendAllText("theme-debug.txt", 
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Theme applied: {(isDark ? "Dark" : "Light")}. BackgroundBrush: {bgBrush?.Color}\n");
                        }
                        catch { }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("WARNING: BackgroundBrush not found in theme dictionary!");
                        try
                        {
                            var exeDir = AppContext.BaseDirectory;
                            var debugPath = System.IO.Path.Combine(exeDir, "theme-debug.txt");
                            File.AppendAllText(debugPath, 
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WARNING: BackgroundBrush not found!\n");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to write theme debug: {ex.Message}");
                        }
                    }
                    
                    // Verify app can find the resource
                    var testBrush = app.TryFindResource("BackgroundBrush") as SolidColorBrush;
                    if (testBrush != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"App.TryFindResource found BackgroundBrush: {testBrush.Color}");
                        try
                        {
                            File.AppendAllText("theme-debug.txt", 
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] App.TryFindResource successful: {testBrush.Color}\n");
                        }
                        catch { }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("WARNING: App.TryFindResource could not find BackgroundBrush!");
                        try
                        {
                            File.AppendAllText("theme-debug.txt", 
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WARNING: App.TryFindResource failed!\n");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to write theme debug: {ex.Message}");
                        }
                    }
                    
                    // Force resource refresh by updating all windows
                    foreach (Window window in app.Windows)
                    {
                        try
                        {
                            window.InvalidateVisual();
                            // Update window background
                            if (window.Resources == null)
                            {
                                window.Resources = new ResourceDictionary();
                            }
                        }
                        catch (Exception winEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error refreshing window: {winEx.Message}");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                // Log error but don't crash
                System.Diagnostics.Debug.WriteLine($"Failed to apply theme: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        private ResourceDictionary CreateThemeDictionaryInternal(bool isDark)
        {
            var dict = new ResourceDictionary();
            
            if (isDark)
            {
                // Dark Theme - Create brushes and freeze them for better performance
                var bgBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
                bgBrush.Freeze();
                dict["BackgroundBrush"] = bgBrush;
                
                var surfaceBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
                surfaceBrush.Freeze();
                dict["SurfaceBrush"] = surfaceBrush;
                
                // Darker neon green for dark mode - better contrast with black text
                var primaryBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x33)); // Darker neon green for better readability
                primaryBrush.Freeze();
                dict["PrimaryBrush"] = primaryBrush;
                
                var primaryHoverBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xB8, 0x2E)); // Slightly darker for hover
                primaryHoverBrush.Freeze();
                dict["PrimaryHoverBrush"] = primaryHoverBrush;
                
                var secondaryTextBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
                secondaryTextBrush.Freeze();
                dict["SecondaryTextBrush"] = secondaryTextBrush;
                
                var primaryTextBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                primaryTextBrush.Freeze();
                dict["PrimaryTextBrush"] = primaryTextBrush;
                
                var borderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
                borderBrush.Freeze();
                dict["BorderBrush"] = borderBrush;
                
                var groupBoxHeaderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
                groupBoxHeaderBrush.Freeze();
                dict["GroupBoxHeaderBrush"] = groupBoxHeaderBrush;
                
                var buttonHoverBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
                buttonHoverBrush.Freeze();
                dict["ButtonHoverBrush"] = buttonHoverBrush;
                
                var buttonPressedBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x4C, 0x4C));
                buttonPressedBrush.Freeze();
                dict["ButtonPressedBrush"] = buttonPressedBrush;
                
                // Navigation-specific brushes for dark mode
                var navigationActiveBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x35, 0x38)); // Slightly lighter than surface
                navigationActiveBrush.Freeze();
                dict["NavigationActiveBrush"] = navigationActiveBrush;
                
                var navigationHoverBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3F)); // Subtle hover that doesn't affect text readability
                navigationHoverBrush.Freeze();
                dict["NavigationHoverBrush"] = navigationHoverBrush;
            }
            else
            {
                // Light Theme
                var bgBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
                bgBrush.Freeze();
                dict["BackgroundBrush"] = bgBrush;
                
                var surfaceBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                surfaceBrush.Freeze();
                dict["SurfaceBrush"] = surfaceBrush;
                
                // Slimy green for light mode
                var primaryBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x8F, 0x47)); // Slimy green
                primaryBrush.Freeze();
                dict["PrimaryBrush"] = primaryBrush;
                
                var primaryHoverBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x7A, 0x3D)); // Darker slimy green
                primaryHoverBrush.Freeze();
                dict["PrimaryHoverBrush"] = primaryHoverBrush;
                
                var secondaryTextBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
                secondaryTextBrush.Freeze();
                dict["SecondaryTextBrush"] = secondaryTextBrush;
                
                var primaryTextBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00));
                primaryTextBrush.Freeze();
                dict["PrimaryTextBrush"] = primaryTextBrush;
                
                var borderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
                borderBrush.Freeze();
                dict["BorderBrush"] = borderBrush;
                
                var groupBoxHeaderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                groupBoxHeaderBrush.Freeze();
                dict["GroupBoxHeaderBrush"] = groupBoxHeaderBrush;
                
                // Subtle hover for light mode
                var buttonHoverBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)); // Slightly darker than surface
                buttonHoverBrush.Freeze();
                dict["ButtonHoverBrush"] = buttonHoverBrush;
                
                var buttonPressedBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
                buttonPressedBrush.Freeze();
                dict["ButtonPressedBrush"] = buttonPressedBrush;
                
                // Navigation-specific brushes for light mode
                var navigationActiveBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF8)); // Slightly lighter than surface
                navigationActiveBrush.Freeze();
                dict["NavigationActiveBrush"] = navigationActiveBrush;
                
                var navigationHoverBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)); // Subtle hover
                navigationHoverBrush.Freeze();
                dict["NavigationHoverBrush"] = navigationHoverBrush;
            }
            
            // Mark as theme dictionary for easy removal
            dict["ThemeBrush"] = dict["PrimaryBrush"];
            dict["IsDarkTheme"] = isDark;
            
            System.Diagnostics.Debug.WriteLine($"Created {(isDark ? "Dark" : "Light")} theme dictionary with {dict.Count} resources");
            
            return dict;
        }

        public void Dispose()
        {
            _registryWatcher?.Dispose();
        }
    }

    // Helper class to watch registry changes
    internal class RegistryKeyWatcher : IDisposable
    {
        private readonly RegistryKey _key;
        private readonly string _valueName;
        private System.Threading.Timer? _timer;
        private object? _lastValue;

        public event EventHandler? ValueChanged;

        public RegistryKeyWatcher(RegistryKey key, string valueName)
        {
            _key = key;
            _valueName = valueName;
            _lastValue = key.GetValue(valueName);

            // Poll every 500ms for changes
            _timer = new System.Threading.Timer(CheckValue, null, 500, 500);
        }

        private void CheckValue(object? state)
        {
            try
            {
                var currentValue = _key.GetValue(_valueName);
                if (!Equals(currentValue, _lastValue))
                {
                    _lastValue = currentValue;
                    ValueChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch
            {
                // Ignore errors
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}
