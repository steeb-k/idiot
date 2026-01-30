using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WIMISODriverInjector.CLI;

namespace WIMISODriverInjector;

/// <summary>
/// Runs the CLI when the app is started with command-line arguments.
/// Called from App.OnLaunched when using the default application definition.
/// </summary>
public static class CliRunner
{
    private static string GetLogFilePath(string fileName)
    {
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logsDir);
        return Path.Combine(logsDir, fileName);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
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

            return await rootCommand.InvokeAsync(args);
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(GetLogFilePath("startup-log.txt"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] FATAL: {ex.Message}\n{ex.StackTrace}\n");
            }
            catch { }

            try
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            catch
            {
                try
                {
                    File.WriteAllText(GetLogFilePath("error-log.txt"), $"Fatal: {ex.Message}\n{ex.StackTrace}");
                }
                catch { }
            }
            return 1;
        }
    }

    private static async Task ProcessFiles(
        FileInfo inputFile,
        FileInfo outputFile,
        DirectoryInfo[] driverDirs,
        FileInfo? logFile,
        bool optimize)
    {
        Console.WriteLine("WIM/ISO Driver Injector v1.0.0");
        Console.WriteLine("================================\n");

        if (!inputFile.Exists)
        {
            Console.WriteLine($"Error: Input file not found: {inputFile.FullName}");
            Environment.Exit(1);
            return;
        }

        var logger = new Core.Logger(logFile?.FullName ?? "injection-log.txt");
        var processor = new Core.ImageProcessor(logger);

        Console.WriteLine($"Input: {inputFile.FullName}");
        Console.WriteLine($"Output: {outputFile.FullName}");
        Console.WriteLine($"Driver directories: {string.Join(", ", driverDirs.Select(d => d.FullName))}");
        Console.WriteLine($"Log file: {logger.LogFilePath}\n");

        bool isISO = inputFile.Extension.Equals(".iso", StringComparison.OrdinalIgnoreCase);
        bool isWIM = inputFile.Extension.Equals(".wim", StringComparison.OrdinalIgnoreCase);

        if (!isISO && !isWIM)
        {
            Console.WriteLine("Error: Input file must be an ISO or WIM file");
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
}
