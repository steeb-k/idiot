using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using WIMISODriverInjector.Core;
using WIMISODriverInjector.CLI;

namespace WIMISODriverInjector
{
    public class Program
    {
        private static string GetLogFilePath(string fileName)
        {
            var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logsDir); // Ensure logs directory exists
            return Path.Combine(logsDir, fileName);
        }

        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                // Write startup log
                try
                {
                    File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Application starting. Args: {string.Join(" ", args)}\n");
                }
                catch { }

                // If no arguments, launch GUI
                if (args.Length == 0)
                {
                    try
                    {
                        File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Launching GUI mode\n");
                    }
                    catch { }

                    // Write a marker to prove we're running the new code
                    try
                    {
                        File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === NEW BUILD - Theme code active ===\n");
                    }
                    catch { }
                    
                    var app = new App();
                    app.InitializeComponent();
                    
                    try
                    {
                        File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] App created and initialized\n");
                    }
                    catch { }

                    // Create theme-debug.txt file FIRST, before any dispatcher calls
                    // Do this in multiple steps with error checking
                    string? themeDebugPath = null;
                    try
                    {
                        themeDebugPath = Path.Combine(Environment.CurrentDirectory, "theme-debug.txt");
                        File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Attempting to create theme-debug.txt at: {themeDebugPath}\n");
                        
                        File.WriteAllText(themeDebugPath, 
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Program.cs: Creating theme-debug.txt file\n");
                        
                        File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] SUCCESS: Created theme-debug.txt file\n");
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR creating theme-debug.txt: {ex.GetType().Name}: {ex.Message}\n");
                            File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Stack trace: {ex.StackTrace}\n");
                        }
                        catch { }
                    }

                    // Ensure theme is applied BEFORE creating window
                    // Use BeginInvoke to avoid blocking, but wait for it
                    try
                    {
                        File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] About to apply theme on UI thread\n");
                        if (File.Exists("theme-debug.txt"))
                        {
                            File.AppendAllText("theme-debug.txt", 
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Program.cs: About to apply theme\n");
                        }
                        else
                        {
                            File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WARNING: theme-debug.txt does not exist!\n");
                        }
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR writing logs: {ex.Message}\n");
                        }
                        catch { }
                    }
                    
                    // Use InvokeAsync and wait for it to complete
                    var themeTask = app.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] On UI thread, ensuring theme is applied\n");
                            File.AppendAllText("theme-debug.txt", 
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Program.cs: On UI thread, checking theme\n");
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR writing theme-debug.txt: {ex.Message}\n");
                            }
                            catch { }
                        }
                        
                        // Always apply theme manually (OnStartup might not fire)
                        try
                        {
                            File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Manually applying theme\n");
                            File.AppendAllText("theme-debug.txt", 
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Program.cs: Manually applying theme\n");
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {ex.Message}\n");
                            }
                            catch { }
                        }
                        
                        // Manually create and apply theme
                        try
                        {
                            var themeManager = new Core.ThemeManager();
                            themeManager.Initialize(app);
                            themeManager.WatchForChanges(app);
                            
                            File.AppendAllText("theme-debug.txt", 
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Program.cs: Theme applied successfully\n");
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR creating ThemeManager: {ex.Message}\n{ex.StackTrace}\n");
                                File.AppendAllText("theme-debug.txt", 
                                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {ex.Message}\n{ex.StackTrace}\n");
                            }
                            catch { }
                        }
                    }, System.Windows.Threading.DispatcherPriority.Send);
                    
                    // Wait for theme to be applied
                    themeTask.Wait();
                    
                    try
                    {
                        File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Theme application complete\n");
                        File.AppendAllText("theme-debug.txt", 
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Program.cs: Theme application complete\n");
                    }
                    catch { }
                    
                    try
                    {
                        File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Running application\n");
                    }
                    catch { }

                    // Run the application - MainWindow will be created in App.OnStartup
                    app.Run();
                    return;
                }

                // Otherwise, run CLI
                RunCLI(args).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Write to log file first
                try
                {
                    File.AppendAllText(GetLogFilePath("startup-log.txt"), 
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] FATAL ERROR: {ex.Message}\n{ex.StackTrace}\n");
                }
                catch { }

                // Write to console if available, otherwise show message box
                try
                {
                    Console.WriteLine($"Fatal error: {ex.Message}");
                    Console.WriteLine($"Stack trace: {ex.StackTrace}");
                }
                catch
                {
                    // If console is not available, try to show a message box
                    try
                    {
                        MessageBox.Show($"Fatal error: {ex.Message}\n\n{ex.StackTrace}", 
                            "WIM/ISO Driver Injector - Error", 
                            MessageBoxButton.OK, 
                            MessageBoxImage.Error);
                    }
                    catch
                    {
                        // Last resort - write to file
                        try
                        {
                            File.WriteAllText(GetLogFilePath("error-log.txt"), 
                                $"Fatal error: {ex.Message}\n\n{ex.StackTrace}");
                        }
                        catch { }
                    }
                }
                Environment.Exit(1);
            }
        }

        private static async Task RunCLI(string[] args)
        {
            var inputFileOption = new Option<FileInfo>(
                aliases: new[] { "--input", "-i" },
                description: "Path to the input ISO or WIM file")
            {
                IsRequired = true
            };

            var outputFileOption = new Option<FileInfo>(
                aliases: new[] { "--output", "-o" },
                description: "Path to the output ISO or WIM file")
            {
                IsRequired = true
            };

            var driversOption = new Option<DirectoryInfo[]>(
                aliases: new[] { "--drivers", "-d" },
                description: "Directory or directories containing driver files (.inf)")
            {
                IsRequired = true,
                AllowMultipleArgumentsPerToken = true
            };

            var logFileOption = new Option<FileInfo?>(
                aliases: new[] { "--log", "-l" },
                description: "Path to the log file (default: injection-log.txt)",
                getDefaultValue: () => new FileInfo("injection-log.txt"));

            var optimizeOption = new Option<bool>(
                aliases: new[] { "--optimize", "--shrink" },
                description: "Optimize and shrink the WIM file after injection",
                getDefaultValue: () => true);

            var rootCommand = new RootCommand("WIM/ISO Driver Injector - Portable tool for injecting drivers into Windows installer images")
            {
                inputFileOption,
                outputFileOption,
                driversOption,
                logFileOption,
                optimizeOption
            };

            rootCommand.SetHandler(async (inputFile, outputFile, driverDirs, logFile, optimize) =>
            {
                await ProcessFiles(inputFile, outputFile, driverDirs, logFile, optimize);
            }, inputFileOption, outputFileOption, driversOption, logFileOption, optimizeOption);

            await rootCommand.InvokeAsync(args);
        }

        private static async Task ProcessFiles(
            FileInfo inputFile,
            FileInfo outputFile,
            DirectoryInfo[] driverDirs,
            FileInfo? logFile,
            bool optimize)
        {
            try
            {
                Console.WriteLine($"WIM/ISO Driver Injector v1.0.0");
                Console.WriteLine($"================================\n");

                if (!inputFile.Exists)
                {
                    Console.WriteLine($"Error: Input file not found: {inputFile.FullName}");
                    Environment.Exit(1);
                    return;
                }

                var logger = new Logger(logFile?.FullName ?? "injection-log.txt");
                var processor = new ImageProcessor(logger);

                Console.WriteLine($"Input: {inputFile.FullName}");
                Console.WriteLine($"Output: {outputFile.FullName}");
                Console.WriteLine($"Driver directories: {string.Join(", ", driverDirs.Select(d => d.FullName))}");
                Console.WriteLine($"Log file: {logger.LogFilePath}\n");

                // Check if input is ISO or WIM
                bool isISO = inputFile.Extension.Equals(".iso", StringComparison.OrdinalIgnoreCase);
                bool isWIM = inputFile.Extension.Equals(".wim", StringComparison.OrdinalIgnoreCase);

                if (!isISO && !isWIM)
                {
                    Console.WriteLine($"Error: Input file must be an ISO or WIM file");
                    Environment.Exit(1);
                    return;
                }

                Console.WriteLine("Starting processing...\n");

                if (isISO)
                {
                    await processor.ProcessISO(inputFile.FullName, outputFile.FullName, 
                        driverDirs.Select(d => d.FullName).ToArray(), optimize);
                }
                else
                {
                    await processor.ProcessWIM(inputFile.FullName, outputFile.FullName, 
                        driverDirs.Select(d => d.FullName).ToArray(), optimize);
                }

                Console.WriteLine("\nProcessing completed successfully!");
                Console.WriteLine($"Check log file for details: {logger.LogFilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nError: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                Environment.Exit(1);
            }
        }
    }
}
