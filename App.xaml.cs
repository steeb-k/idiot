using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.WindowsAppRuntime;

namespace WIMISODriverInjector;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    static App()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID("WIMISODriverInjector.Idiot.1");
        }
        catch { }

        try
        {
            var initResult = DeploymentManager.Initialize();
            if (initResult.Status != DeploymentStatus.Ok)
                System.Diagnostics.Debug.WriteLine($"Windows App SDK initialization failed: {initResult.Status}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Windows App SDK init: {ex.Message}");
        }
    }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var userArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
        // When run via "dotnet run", args are e.g. ["run"] or [path to .dll]. Don't treat that as CLI.
        bool isDotnetRunArtifact = userArgs.Length == 1 &&
            (userArgs[0].Equals("run", StringComparison.OrdinalIgnoreCase) ||
             userArgs[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        if (userArgs.Length > 0 && !isDotnetRunArtifact)
        {
            var exitCode = CliRunner.RunAsync(userArgs).GetAwaiter().GetResult();
            Environment.Exit(exitCode);
            return;
        }

        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        var startupLogPath = Path.Combine(logsDir, "startup-log.txt");

        try
        {
            Directory.CreateDirectory(logsDir);
            File.AppendAllText(startupLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Started (BaseDirectory: {AppContext.BaseDirectory})\n");
        }
        catch { }

        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MainWindow: {ex.Message}");
            try
            {
                File.AppendAllText(startupLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR creating MainWindow: {ex.Message}\n{ex.StackTrace}\n");
            }
            catch { }
        }
    }
}
