using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WIMISODriverInjector.Core;

namespace WIMISODriverInjector
{
    public partial class App : Application
    {
        private ThemeManager? _themeManager;

        private static string GetLogFilePath(string fileName)
        {
            var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logsDir); // Ensure logs directory exists
            return Path.Combine(logsDir, fileName);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                // Handle unhandled exceptions
                this.DispatcherUnhandledException += App_DispatcherUnhandledException;
                AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

                // Load resources first
                try
                {
                    base.OnStartup(e);
                }
                catch (System.Windows.ResourceReferenceKeyNotFoundException)
                {
                    // Resource dictionary might not load in single-file mode, continue anyway
                    base.OnStartup(e);
                }

                // Initialize theme manager AFTER base.OnStartup so resources are loaded
                try
                {
                    File.AppendAllText("theme-debug.txt", 
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] App.OnStartup: About to create ThemeManager\n");
                    
                    _themeManager = new ThemeManager();
                    
                    File.AppendAllText("theme-debug.txt", 
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] App.OnStartup: ThemeManager created, calling Initialize\n");
                    
                    _themeManager.Initialize(this);
                    
                    File.AppendAllText("theme-debug.txt", 
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] App.OnStartup: ThemeManager initialized, setting up watcher\n");
                    
                    _themeManager.WatchForChanges(this);
                    _themeManager.ThemeChanged += (sender, isDark) =>
                    {
                        // Refresh all windows when theme changes
                        RefreshAllWindows();
                        System.Diagnostics.Debug.WriteLine($"Theme changed to: {(isDark ? "Dark" : "Light")}");
                    };
                    
                    File.AppendAllText("theme-debug.txt", 
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] App.OnStartup: ThemeManager setup complete\n");
                }
                catch (Exception themeEx)
                {
                    // If theme initialization fails, continue with default theme
                    System.Diagnostics.Debug.WriteLine($"Theme initialization failed: {themeEx.Message}");
                    System.Diagnostics.Debug.WriteLine($"Stack trace: {themeEx.StackTrace}");
                    
                    try
                    {
                        File.AppendAllText("theme-debug.txt", 
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR in App.OnStartup: {themeEx.Message}\n{themeEx.StackTrace}\n");
                    }
                    catch { }
                }
                
                // Check if running in CLI mode
                if (e.Args.Length > 0)
                {
                    // CLI mode - handled by Program.cs
                    Shutdown();
                    return;
                }
                
                // GUI mode - create and show MainWindow
                try
                {
                    File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] App.OnStartup: Creating MainWindow\n");
                    
                    var mainWindow = new MainWindow();
                    mainWindow.Show();
                    
                    File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] App.OnStartup: MainWindow created and shown\n");
                }
                catch (Exception windowEx)
                {
                    File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR in App.OnStartup creating MainWindow: {windowEx.Message}\n{windowEx.StackTrace}\n");
                    MessageBox.Show($"Error creating window:\n\n{windowEx.Message}\n\n{windowEx.StackTrace}", 
                        "Window Creation Error", 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Error);
                    Shutdown(1);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    MessageBox.Show($"Error during startup:\n\n{ex.Message}\n\n{ex.StackTrace}", 
                        "Startup Error", 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Error);
                }
                catch
                {
                    // If MessageBox fails, try console
                    try
                    {
                        Console.WriteLine($"Error during startup: {ex.Message}");
                        Console.WriteLine(ex.StackTrace);
                    }
                    catch { }
                }
                Shutdown(1);
            }
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                MessageBox.Show($"An unhandled exception occurred:\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}", 
                    "WIM/ISO Driver Injector - Error", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Error);
            }
            catch { }
            
            e.Handled = true;
            Shutdown(1);
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                var message = ex != null ? ex.Message : "Unknown error";
                var stackTrace = ex != null ? ex.StackTrace : "";
                
                MessageBox.Show($"A fatal error occurred:\n\n{message}\n\n{stackTrace}", 
                    "WIM/ISO Driver Injector - Fatal Error", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Error);
            }
            catch { }
        }

        private void RefreshAllWindows()
        {
            foreach (Window window in Windows)
            {
                try
                {
                    // Force UI refresh by invalidating visual
                    window.InvalidateVisual();
                    // Force property updates
                    var bg = window.Background;
                    window.Background = null;
                    window.Background = bg;
                }
                catch { }
            }
        }
    }
}
