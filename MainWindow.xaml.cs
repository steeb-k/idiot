using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI;
using WinRT;
using WinRT.Interop;
using WIMISODriverInjector.Core;

namespace WIMISODriverInjector;

public sealed partial class MainWindow : Window
{
    private static readonly HttpClient HttpClient = new();
    private const string GitHubReleasesApiUrl = "https://api.github.com/repos/steeb-k/idiot/releases";
    private const string GitHubReleasesPageUrl = "https://github.com/steeb-k/idiot/releases/latest";
    private readonly ObservableCollection<string> _driverDirectories = new();
    private readonly ObservableCollection<string> _wimFiles = new();
    private readonly ObservableCollection<VersionItem> _versionItems = new();
    private readonly ObservableCollection<string> _logFiles = new();
    private string? _contextMenuLogPath;

    private Logger? _logger;
    private ImageProcessor? _processor;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;

    private string? _selectedInstallWimPath;
    private List<WIMIndex>? _installWimIndexes;
    private string? _mountedIsoPath;
    private string? _mountedIsoDrive;
    
    // Mica backdrop
    private MicaController? _micaController;
    private SystemBackdropConfiguration? _configurationSource;

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            SetupMicaBackdrop();
            ConfigureWindow();
            PopulateScratchDrives();
            LoadScratchDrivePreference();

            DriverDirectoriesListBox.ItemsSource = _driverDirectories;
            WimFilesListBox.ItemsSource = _wimFiles;
            VersionListBox.ItemsSource = _versionItems;
            LogFilesListBox.ItemsSource = _logFiles;

            ScratchDriveComboBox.SelectionChanged += (s, e) => SaveScratchDrivePreference();

            var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logsDir);

            ImageSelectionButton.Tag = "ImageSelection";
            ShowSection("ImageSelection");
            RefreshLogFiles();

            SetAboutVersionText();
            _ = CheckForUpdatesOnStartupAsync();

            Closed += Window_Closed;
        }
        catch (Exception ex)
        {
            _ = ThemedMessageBox.ShowAsync(this, $"Error initializing MainWindow:\n\n{ex.Message}\n\n{ex.StackTrace}", "Initialization Error", true);
            throw;
        }
    }

    private void ConfigureWindow()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            if (AppWindow != null)
            {
                AppWindow.Title = "I.D.I.O.T. - Image Driver Integration & Optimization Tool";
                
                // Set the window icon for taskbar preview
                var iconPath = Path.Combine(AppContext.BaseDirectory, "idiotLogo.ico");
                if (File.Exists(iconPath))
                {
                    AppWindow.SetIcon(iconPath);
                }
                if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
                {
                    AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                    var root = Content as FrameworkElement;
                    var isDark = root?.ActualTheme == ElementTheme.Dark;
                    AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                    AppWindow.TitleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                    if (isDark)
                    {
                        AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
                        AppWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 128, 128, 128);
                    }
                    else
                    {
                        AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);
                        AppWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 128, 128, 128);
                    }
                }
                var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
                var workArea = displayArea.WorkArea;
                var centerX = workArea.X + (workArea.Width - 1200) / 2;
                var centerY = workArea.Y + (workArea.Height - 800) / 2;
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(centerX, centerY, 1200, 800));
            }
        }
        catch { }
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        var originalContent = CheckUpdatesButton.Content;
        CheckUpdatesButton.Content = "Checking...";

        try
        {
            var currentVersionText = GetAppVersionFromJson();
            var currentVersion = ParseVersion(currentVersionText);

            var latestResult = await GetLatestReleaseInfoAsync();
            if (latestResult == null || latestResult.Info == null)
            {
                var errorMessage = latestResult?.ErrorMessage ?? "Unable to check for updates right now.";
                await ThemedMessageBox.ShowAsync(this, errorMessage, "Update Check", false);
                return;
            }

            var latestInfo = latestResult.Info;
            var latestVersion = latestInfo.Version;
            if (latestVersion > currentVersion)
            {
                await PromptUpdateAndInstallAsync(currentVersionText, latestInfo);
            }
            else
            {
                await ThemedMessageBox.ShowAsync(this, $"You're up to date.\n\nCurrent: {currentVersionText}", "No Updates", false);
            }
        }
        catch (Exception ex)
        {
            await ThemedMessageBox.ShowAsync(this, $"Update check failed:\n\n{ex.Message}", "Update Check", true);
        }
        finally
        {
            CheckUpdatesButton.Content = originalContent;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private static Version ParseVersion(string versionText)
    {
        var normalized = versionText.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(1);
        }

        var basePart = normalized.Split('-', 2)[0];
        return Version.TryParse(basePart, out var parsed) ? parsed : new Version(0, 0, 0);
    }

    private static async Task<LatestReleaseResult?> GetLatestReleaseInfoAsync()
    {
        if (!HttpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("idiot-update-checker");
        }
        if (!HttpClient.DefaultRequestHeaders.Accept.Any())
        {
            HttpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        try
        {
            using var response = await HttpClient.GetAsync(GitHubReleasesApiUrl);
            if (response.IsSuccessStatusCode)
            {
                using var stream = await response.Content.ReadAsStreamAsync();
                using var document = await JsonDocument.ParseAsync(stream);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                {
                    return new LatestReleaseResult { ErrorMessage = "No GitHub releases found." };
                }

                JsonElement? selectedRelease = null;
                foreach (var release in root.EnumerateArray())
                {
                    if (release.TryGetProperty("draft", out var draftElement) && draftElement.GetBoolean())
                    {
                        continue;
                    }

                    selectedRelease = release;
                    break;
                }

                if (selectedRelease == null)
                {
                    return new LatestReleaseResult { ErrorMessage = "No published GitHub releases found." };
                }

                var releaseValue = selectedRelease.Value;
                var tagName = releaseValue.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
                var releaseUrl = releaseValue.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;
                var installerUrl = ExtractInstallerUrl(releaseValue);
                var versionText = tagName ?? "";
                var version = ParseVersion(versionText);

                return new LatestReleaseResult
                {
                    Info = new LatestReleaseInfo
                    {
                        Version = version,
                        VersionText = versionText,
                        ReleaseUrl = releaseUrl ?? GitHubReleasesPageUrl,
                        InstallerUrl = installerUrl
                    }
                };
            }

            var status = (int)response.StatusCode;
            var reason = response.ReasonPhrase ?? "";
            var error = $"Update check failed (API): {status} {reason}.";
            var fallback = await TryFallbackReleasePageAsync();
            if (fallback != null)
            {
                return new LatestReleaseResult { Info = fallback, ErrorMessage = error };
            }

            return new LatestReleaseResult { ErrorMessage = error };
        }
        catch (Exception ex)
        {
            var fallback = await TryFallbackReleasePageAsync();
            if (fallback != null)
            {
                return new LatestReleaseResult { Info = fallback, ErrorMessage = $"Update check fell back after error: {ex.Message}" };
            }

            return new LatestReleaseResult { ErrorMessage = $"Unable to check for updates: {ex.Message}" };
        }
    }

    private static async Task<LatestReleaseInfo?> TryFallbackReleasePageAsync()
    {
        try
        {
            using var response = await HttpClient.GetAsync(GitHubReleasesPageUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var finalUri = response.RequestMessage?.RequestUri?.ToString() ?? "";
            var versionText = ExtractVersionFromUrl(finalUri);
            if (string.IsNullOrWhiteSpace(versionText))
            {
                return null;
            }

            return new LatestReleaseInfo
            {
                Version = ParseVersion(versionText),
                VersionText = versionText,
                ReleaseUrl = finalUri
            };
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractVersionFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var lastSegment = url.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
        if (lastSegment.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            lastSegment = lastSegment.Substring(1);
        }
        return lastSegment;
    }

    private static string? ExtractInstallerUrl(JsonElement releaseElement)
    {
        if (!releaseElement.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assetsElement.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Contains("installer", StringComparison.OrdinalIgnoreCase))
            {
                var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }
        }

        return null;
    }

    private async Task PromptUpdateAndInstallAsync(string currentVersionText, LatestReleaseInfo latestInfo)
    {
        var message = $"A newer version is available.\n\nCurrent: {currentVersionText}\nLatest: {latestInfo.VersionText}\n\nDo you want to download and install it now?";

        var dialog = new ContentDialog
        {
            Title = "Update Available",
            Content = message,
            PrimaryButtonText = "Download & Install",
            CloseButtonText = "Later",
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            UpdateAvailableText.Visibility = Visibility.Visible;
            return;
        }

        UpdateAvailableText.Visibility = Visibility.Collapsed;
        await DownloadInstallAndCloseAsync(latestInfo);
    }

    private async Task DownloadInstallAndCloseAsync(LatestReleaseInfo latestInfo)
    {
        if (string.IsNullOrWhiteSpace(latestInfo.InstallerUrl))
        {
            await ThemedMessageBox.ShowAsync(this, "No installer asset was found for the latest release.", "Update", false);
            return;
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"idiot-{latestInfo.VersionText}-installer.exe");

        try
        {
            CheckUpdatesButton.IsEnabled = false;
            CheckUpdatesButton.Content = "Downloading...";

            using var response = await HttpClient.GetAsync(latestInfo.InstallerUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = tempFile,
                UseShellExecute = true
            };

            Process.Start(startInfo);

            Close();
        }
        catch (Exception ex)
        {
            await ThemedMessageBox.ShowAsync(this, $"Failed to download or start the installer:\n\n{ex.Message}", "Update", true);
        }
        finally
        {
            CheckUpdatesButton.Content = "Check for Updates";
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private sealed class LatestReleaseInfo
    {
        public required Version Version { get; init; }
        public required string VersionText { get; init; }
        public required string ReleaseUrl { get; init; }
        public string? InstallerUrl { get; init; }
    }

    private sealed class LatestReleaseResult
    {
        public LatestReleaseInfo? Info { get; init; }
        public string? ErrorMessage { get; init; }
    }

    private void SetAboutVersionText()
    {
        try
        {
            var version = GetAppVersionFromJson();
            AboutVersionText.Text = $"Version {version}";
        }
        catch
        {
            AboutVersionText.Text = "Version Unknown";
        }
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            await Task.Delay(2000);

            var currentVersionText = GetAppVersionFromJson();
            var currentVersion = ParseVersion(currentVersionText);

            var latestResult = await GetLatestReleaseInfoAsync();
            if (latestResult?.Info == null)
            {
                return;
            }

            var latestInfo = latestResult.Info;
            if (latestInfo.Version > currentVersion)
            {
                await PromptUpdateAndInstallAsync(currentVersionText, latestInfo);
            }
        }
        catch
        {
            // Silently fail startup update check
        }
    }

    private static string GetAppVersionFromJson()
    {
        var versionFilePath = Path.Combine(AppContext.BaseDirectory, "version.json");
        if (File.Exists(versionFilePath))
        {
            using var stream = File.OpenRead(versionFilePath);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("version", out var versionElement))
            {
                var versionValue = versionElement.GetString();
                if (!string.IsNullOrWhiteSpace(versionValue))
                {
                    return versionValue;
                }
            }
        }

        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
        return assemblyVersion?.ToString(3) ?? "Unknown";
    }

    private void SetupMicaBackdrop()
    {
        try
        {
            // Check if Mica is supported
            if (MicaController.IsSupported())
            {
                _micaController = new MicaController();
                _configurationSource = new SystemBackdropConfiguration();
                
                // Initialize the configuration
                this.Activated += (s, e) =>
                {
                    if (_micaController != null && _configurationSource != null)
                    {
                        _configurationSource.IsInputActive = true;
                        _micaController.SetSystemBackdropConfiguration(_configurationSource);
                    }
                };
                
                // Add as backdrop
                _micaController.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
                _micaController.SetSystemBackdropConfiguration(_configurationSource);
                
                // Hook up theme changed event
                ((FrameworkElement)Content).ActualThemeChanged += (s, e) =>
                {
                    if (_configurationSource != null)
                    {
                        _configurationSource.Theme = ((FrameworkElement)Content).ActualTheme switch
                        {
                            ElementTheme.Dark => SystemBackdropTheme.Dark,
                            ElementTheme.Light => SystemBackdropTheme.Light,
                            _ => SystemBackdropTheme.Default
                        };
                    }
                };
            }
        }
        catch { }
    }

    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            var section = button.Tag?.ToString() ?? "";
            if (section == "Active")
            {
                if (button == ImageSelectionButton) section = "ImageSelection";
                else if (button == DriverSelectionButton) section = "Options";
                else if (button == OptionsButton) section = "Finalize";
                else if (button == CleanupButton) section = "Cleanup";
                else if (button == LogsButton) section = "Logs";
                else if (button == AboutButton) section = "About";
            }
            if (!string.IsNullOrEmpty(section))
                ShowSection(section);
        }
    }

    private void ShowSection(string section)
    {
        // Reset all buttons to default appearance
        var transparentBrush = new SolidColorBrush();
        transparentBrush.Color = Color.FromArgb(0, 0, 0, 0);
        
        ImageSelectionButton.Tag = "ImageSelection";
        ImageSelectionButton.Background = transparentBrush;
        ImageSelectionIndicator.BorderBrush = transparentBrush;
        DriverSelectionButton.Tag = "Options";
        DriverSelectionButton.Background = transparentBrush;
        DriverSelectionIndicator.BorderBrush = transparentBrush;
        OptionsButton.Tag = "Finalize";
        OptionsButton.Background = transparentBrush;
        OptionsIndicator.BorderBrush = transparentBrush;
        CleanupButton.Tag = "Cleanup";
        CleanupButton.Background = transparentBrush;
        CleanupIndicator.BorderBrush = transparentBrush;
        LogsButton.Tag = "Logs";
        LogsButton.Background = transparentBrush;
        LogsIndicator.BorderBrush = transparentBrush;
        AboutButton.Tag = "About";
        AboutButton.Background = transparentBrush;
        AboutIndicator.BorderBrush = transparentBrush;

        ImageSelectionPanel.Visibility = Visibility.Collapsed;
        OptionsPanel.Visibility = Visibility.Collapsed;
        FinalizePanel.Visibility = Visibility.Collapsed;
        CleanupPanel.Visibility = Visibility.Collapsed;
        LogsPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Collapsed;
        MainScrollViewer.Visibility = Visibility.Visible;

        var navigationActiveBrush = Application.Current.Resources["NavigationActiveBrush"] as SolidColorBrush;
        var primaryBrush = Application.Current.Resources["PrimaryBrush"] as SolidColorBrush;

        switch (section)
        {
            case "ImageSelection":
                ImageSelectionPanel.Visibility = Visibility.Visible;
                ImageSelectionButton.Tag = "Active";
                if (navigationActiveBrush != null) ImageSelectionButton.Background = navigationActiveBrush;
                if (primaryBrush != null) ImageSelectionIndicator.BorderBrush = primaryBrush;
                break;
            case "Options":
                if (DriverDirectoriesListBox.ItemsSource == null)
                    DriverDirectoriesListBox.ItemsSource = _driverDirectories;
                OptionsPanel.Visibility = Visibility.Visible;
                DriverSelectionButton.Tag = "Active";
                if (navigationActiveBrush != null) DriverSelectionButton.Background = navigationActiveBrush;
                if (primaryBrush != null) DriverSelectionIndicator.BorderBrush = primaryBrush;
                break;
            case "Finalize":
                FinalizePanel.Visibility = Visibility.Visible;
                OptionsButton.Tag = "Active";
                if (navigationActiveBrush != null) OptionsButton.Background = navigationActiveBrush;
                if (primaryBrush != null) OptionsIndicator.BorderBrush = primaryBrush;
                break;
            case "Cleanup":
                CleanupPanel.Visibility = Visibility.Visible;
                CleanupButton.Tag = "Active";
                if (navigationActiveBrush != null) CleanupButton.Background = navigationActiveBrush;
                if (primaryBrush != null) CleanupIndicator.BorderBrush = primaryBrush;
                break;
            case "Logs":
                MainScrollViewer.Visibility = Visibility.Collapsed;
                LogsPanel.Visibility = Visibility.Visible;
                LogsButton.Tag = "Active";
                if (navigationActiveBrush != null) LogsButton.Background = navigationActiveBrush;
                if (primaryBrush != null) LogsIndicator.BorderBrush = primaryBrush;
                RefreshLogFiles();
                break;
            case "About":
                AboutPanel.Visibility = Visibility.Visible;
                AboutButton.Tag = "Active";
                if (navigationActiveBrush != null) AboutButton.Background = navigationActiveBrush;
                if (primaryBrush != null) AboutIndicator.BorderBrush = primaryBrush;
                break;
        }
    }

    private async void BrowseInputFile_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var path = NativeFileDialog.PickOpenFile(hwnd, "Select ISO or WIM file",
            "ISO/WIM files (*.iso;*.wim)|*.iso;*.wim|ISO files (*.iso)|*.iso|WIM files (*.wim)|*.wim|All files (*.*)|*.*");
        if (string.IsNullOrEmpty(path)) return;

        if (!string.IsNullOrEmpty(_mountedIsoPath) && !string.IsNullOrEmpty(_mountedIsoDrive))
        {
            try { await UnmountISO(_mountedIsoPath); } catch { }
            _mountedIsoPath = null;
            _mountedIsoDrive = null;
        }

        InputFileTextBox.Text = path;
        
        // Always update output filename when new input file is selected
        var inputFile = new FileInfo(path);
        var outputName = Path.ChangeExtension(inputFile.Name, null) + "_injected" + inputFile.Extension;
        OutputFileTextBox.Text = Path.Combine(inputFile.DirectoryName ?? "", outputName);

        if (path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            await ExtractIsoFile(path);
        else if (path.EndsWith(".wim", StringComparison.OrdinalIgnoreCase))
        {
            _wimFiles.Clear();
            _wimFiles.Add(path);
            WimFilesPanel.Visibility = Visibility.Visible;
            VolumeLabelPanel.Visibility = Visibility.Collapsed; // Hide volume label for WIM files
            if (Path.GetFileName(path).Equals("install.wim", StringComparison.OrdinalIgnoreCase))
                await LoadWimVersions(path);
        }
    }

    private async Task ExtractIsoFile(string isoPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(_mountedIsoPath) && !string.IsNullOrEmpty(_mountedIsoDrive))
                await UnmountISO(_mountedIsoPath);

            ExtractionProgressPanel.Visibility = Visibility.Visible;
            WimFilesPanel.Visibility = Visibility.Collapsed;
            ExtractionStatusText.Text = "Mounting ISO file...";
            InputFileTextBox.IsEnabled = false;

            _mountedIsoDrive = await MountISO(isoPath);
            _mountedIsoPath = isoPath;
            if (string.IsNullOrEmpty(_mountedIsoDrive))
                throw new Exception("Failed to mount ISO - no drive letter returned");

            ExtractionStatusText.Text = "Scanning for WIM files...";
            var mountedPath = $"{_mountedIsoDrive}:\\";
            
            // Get and display the volume label from the mounted ISO
            try
            {
                var driveInfo = new DriveInfo(_mountedIsoDrive);
                var volumeLabel = driveInfo.VolumeLabel;
                VolumeLabelTextBox.Text = string.IsNullOrWhiteSpace(volumeLabel) ? "" : volumeLabel;
                VolumeLabelPanel.Visibility = Visibility.Visible;
            }
            catch
            {
                VolumeLabelTextBox.Text = "";
                VolumeLabelPanel.Visibility = Visibility.Visible;
            }
            
            var wimFiles = Directory.GetFiles(mountedPath, "*.wim", SearchOption.AllDirectories);
            _wimFiles.Clear();
            foreach (var w in wimFiles)
                _wimFiles.Add(Path.GetRelativePath(mountedPath, w));

            ExtractionProgressPanel.Visibility = Visibility.Collapsed;
            WimFilesPanel.Visibility = Visibility.Visible;
            InputFileTextBox.IsEnabled = true;

            var installWim = wimFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("install.wim", StringComparison.OrdinalIgnoreCase));
            if (installWim != null)
            {
                _selectedInstallWimPath = installWim;
                await LoadWimVersions(installWim);
                VersionSelectionPanel.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            ExtractionProgressPanel.Visibility = Visibility.Collapsed;
            InputFileTextBox.IsEnabled = true;
            if (!string.IsNullOrEmpty(_mountedIsoPath))
            {
                try { await UnmountISO(_mountedIsoPath); } catch { }
                _mountedIsoPath = null;
                _mountedIsoDrive = null;
            }
            await ThemedMessageBox.ShowAsync(this, $"Failed to mount ISO:\n\n{ex.Message}", "Error", true);
        }
    }

    private static async Task<string> MountISO(string isoPath)
    {
        var script = $@"
            $iso = Mount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}' -PassThru
            $driveLetter = ($iso | Get-Volume).DriveLetter
            Write-Output $driveLetter
        ";
        var processInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(processInfo);
        if (process == null) throw new Exception("Failed to start PowerShell mount process");
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            var err = await process.StandardError.ReadToEndAsync();
            throw new Exception($"ISO mount failed: {err}");
        }
        var driveLetter = output.Trim();
        if (string.IsNullOrEmpty(driveLetter)) throw new Exception("ISO mounted but no drive letter returned");
        return driveLetter;
    }

    private static async Task UnmountISO(string isoPath)
    {
        var script = $"Dismount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}'";
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        if (process != null) await process.WaitForExitAsync();
    }

    private async Task LoadWimVersions(string wimPath)
    {
        try
        {
            var tempLogPath = Path.Combine(Path.GetTempPath(), "wim-info-log.txt");
            var tempLogger = new Logger(tempLogPath);
            var processor = new ImageProcessor(tempLogger, null, null, null);
            _installWimIndexes = await processor.GetWIMIndexes(wimPath);
            _versionItems.Clear();
            foreach (var index in _installWimIndexes)
                _versionItems.Add(new VersionItem { Index = index.Index, Name = index.Name, Display = $"Index {index.Index}: {index.Name}", IsSelected = true });
        }
        catch (Exception ex)
        {
            await ThemedMessageBox.ShowAsync(this, $"Failed to load WIM versions:\n\n{ex.Message}", "Error", true);
        }
    }

    private void BrowseOutputFile_Click(object sender, RoutedEventArgs e)
    {
        var inputPath = InputFileTextBox.Text;
        var isISO = !string.IsNullOrEmpty(inputPath) && inputPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
        var isWIM = !string.IsNullOrEmpty(inputPath) && inputPath.EndsWith(".wim", StringComparison.OrdinalIgnoreCase);
        var filter = isISO ? "ISO files (*.iso)|*.iso|All files (*.*)|*.*" : isWIM ? "WIM files (*.wim)|*.wim|All files (*.*)|*.*" : "All files (*.*)|*.*";
        var path = NativeFileDialog.PickSaveFile(WindowNative.GetWindowHandle(this), "Save output file",
            Path.GetFileName(OutputFileTextBox.Text), isISO ? "iso" : "wim", filter);
        if (!string.IsNullOrEmpty(path))
            OutputFileTextBox.Text = path;
    }

    private void AddDriverDirectory_Click(object sender, RoutedEventArgs e)
    {
        var path = NativeFileDialog.PickFolder(WindowNative.GetWindowHandle(this), "Select folder containing driver files (.inf)");
        if (!string.IsNullOrEmpty(path) && !_driverDirectories.Contains(path))
            _driverDirectories.Add(path);
    }

    private void RemoveDriverDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (DriverDirectoriesListBox.SelectedItem is string selected)
            _driverDirectories.Remove(selected);
    }

    private void LogFilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogFilesListBox.SelectedItem is string logFile && File.Exists(logFile))
        {
            try { LogContentTextBlock.Text = File.ReadAllText(logFile); }
            catch (Exception ex) { LogContentTextBlock.Text = $"Error reading log file: {ex.Message}"; }
        }
    }

    private void LogFilesListBox_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(LogFilesListBox);
        if (!point.Properties.IsRightButtonPressed)
            return;
        var position = point.Position;
        foreach (var item in _logFiles)
        {
            var container = LogFilesListBox.ContainerFromItem(item) as ListViewItem;
            if (container == null) continue;
            var transform = container.TransformToVisual(LogFilesListBox);
            var bounds = new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight);
            var rect = transform.TransformBounds(bounds);
            if (rect.Contains(position))
            {
                _contextMenuLogPath = item;
                LogFilesListBox.SelectedItem = item;
                return;
            }
        }
    }

    private string? GetLogPathForMenuAction()
    {
        var path = _contextMenuLogPath;
        if (string.IsNullOrEmpty(path) && LogFilesListBox.SelectedItem is string selected)
            path = selected;
        if (string.IsNullOrEmpty(path) && LogFilesListBox.Items.Count > 0 && LogFilesListBox.Items[0] is string first)
            path = first;
        return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
    }

    private void LogFileCopyText_Click(object sender, RoutedEventArgs e)
    {
        var path = GetLogPathForMenuAction();
        if (path == null)
            return;
        try
        {
            var text = File.ReadAllText(path);
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            _ = ThemedMessageBox.ShowAsync(this, $"Could not copy to clipboard: {ex.Message}", "Copy failed", true);
        }
    }

    private void LogFileShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var path = GetLogPathForMenuAction();
        if (path == null)
            return;
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                _ = ThemedMessageBox.ShowAsync(this, "Log folder not found.", "Show in Explorer", true);
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
                WorkingDirectory = folder
            });
        }
        catch (Exception ex)
        {
            try
            {
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = folder,
                        UseShellExecute = true
                    });
                }
                else
                    throw;
            }
            catch
            {
                _ = ThemedMessageBox.ShowAsync(this, $"Could not open Explorer: {ex.Message}", "Open failed", true);
            }
        }
    }

    private void RefreshLogFiles()
    {
        _logFiles.Clear();
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        var logDir = Directory.Exists(logsDir) ? logsDir : AppContext.BaseDirectory;
        var files = Directory.Exists(logDir)
            ? Directory.GetFiles(logDir, "*.txt")
                .Where(f => !string.Equals(Path.GetFileName(f), "startup-log.txt", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                .ToArray()
            : Array.Empty<string>();
        foreach (var f in files)
            _logFiles.Add(f);
        if (_logFiles.Count > 0 && LogFilesListBox.SelectedItem == null)
            LogFilesListBox.SelectedIndex = 0;
    }

    private async void StartProcessing_Click(object sender, RoutedEventArgs e)
    {
        if (_processingTask != null && !_processingTask.IsCompleted)
        {
            _logger?.LogInfo("Cancellation requested by user...");
            _cancellationTokenSource?.Cancel();
            return;
        }

        if (string.IsNullOrWhiteSpace(InputFileTextBox.Text))
        { await ThemedMessageBox.ShowAsync(this, "Please select an input file.", "Validation Error"); return; }
        if (!File.Exists(InputFileTextBox.Text))
        { await ThemedMessageBox.ShowAsync(this, "Input file does not exist.", "Validation Error", true); return; }
        if (string.IsNullOrWhiteSpace(OutputFileTextBox.Text))
        { await ThemedMessageBox.ShowAsync(this, "Please specify an output file.", "Validation Error"); return; }
        if (_driverDirectories.Count == 0)
        { await ThemedMessageBox.ShowAsync(this, "Please add at least one driver directory.", "Validation Error"); return; }

        var selectedVersions = _versionItems.Where(v => v.IsSelected).Select(v => v.Index).ToList();
        if (_selectedInstallWimPath != null && selectedVersions.Count == 0)
        { await ThemedMessageBox.ShowAsync(this, "Please select at least one Windows version to include.", "Validation Error"); return; }

        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;
        SetProcessingState(false, true);

        try
        {
            // Always generate fresh log filename based on current date/time when SQUIRT is clicked
            var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logsDir);
            var logFilePath = Path.Combine(logsDir, $"injection-log-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt");

            _logger = new Logger(logFilePath);
            _logger.LogInfo("=== Processing Started ===");
            _logger.LogInfo($"Input: {InputFileTextBox.Text}");
            _logger.LogInfo($"Output: {OutputFileTextBox.Text}");

            Action<string> guiLog = msg => DispatcherQueue.TryEnqueue(() => StatusTextBlock.Text = msg);
            Action<string> statusUpdate = status => DispatcherQueue.TryEnqueue(() => StatusTextBlock.Text = status);
            Action<string> phaseUpdate = phase => DispatcherQueue.TryEnqueue(() =>
            {
                PhaseTextBlock.Text = string.IsNullOrEmpty(phase) ? "Progress" : phase;
            });

            string? scratchDirectory = null;
            if (ScratchDriveComboBox.SelectedItem is ScratchDriveItem sel)
                scratchDirectory = sel.Path;

            _processor = new ImageProcessor(_logger, guiLog, statusUpdate, phaseUpdate, scratchDirectory);

            var inputPath = InputFileTextBox.Text;
            var outputPath = OutputFileTextBox.Text;
            var driverDirs = _driverDirectories.ToArray();
            var useMaxCompression = CompressionComboBox.SelectedIndex == 1; // 0 = Fast, 1 = Maximum
            var volumeLabel = VolumeLabelTextBox.Text; // Capture volume label on UI thread

            StatusTextBlock.Text = "Processing...";
            ProgressBar.IsIndeterminate = true;

            _processingTask = Task.Run(async () =>
            {
                try
                {
                    List<int>? selectedVersionsList = null;
                    if (_selectedInstallWimPath != null && _versionItems.Count > 0)
                        selectedVersionsList = _versionItems.Where(v => v.IsSelected).Select(v => v.Index).ToList();

                    if (inputPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                    {
                        Dictionary<string, List<int>>? selectedByWim = null;
                        if (_selectedInstallWimPath != null && _versionItems.Count > 0)
                        {
                            selectedByWim = new Dictionary<string, List<int>>();
                            var installWimFileName = Path.GetFileName(_selectedInstallWimPath);
                            var list = _versionItems.Where(v => v.IsSelected).Select(v => v.Index).ToList();
                            if (list.Count > 0) selectedByWim[installWimFileName] = list;
                        }
                        string? mountedDrive = null;
                        if (!string.IsNullOrEmpty(_mountedIsoDrive) && inputPath.Equals(_mountedIsoPath, StringComparison.OrdinalIgnoreCase))
                        {
                            mountedDrive = _mountedIsoDrive;
                            _logger?.LogInfo($"Using already-mounted ISO drive: {mountedDrive}:\\");
                        }
                        await _processor.ProcessISO(inputPath, outputPath, driverDirs, useMaxCompression, cancellationToken, selectedByWim, mountedDrive, volumeLabel: volumeLabel);
                    }
                    else
                    {
                        var wimFileName = Path.GetFileName(inputPath);
                        if (wimFileName.Equals("boot.wim", StringComparison.OrdinalIgnoreCase))
                        { selectedVersionsList = null; _logger?.LogInfo("boot.wim detected - will process ALL indexes"); }
                        await _processor.ProcessWIM(inputPath, outputPath, driverDirs, useMaxCompression, cancellationToken, selectedVersionsList);
                    }

                    // Show cleanup status and disable cancel button during unmount phase
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        StatusTextBlock.Text = "Process completed successfully! Cleaning up...";
                        StartProcessingButton.IsEnabled = false;
                    });

                    // Process any deferred unmounts (runs even if deferUnmounts was false - handles retry failures)
                    await _processor.ProcessDeferredUnmountsAsync(cancellationToken);

                    // Now run cleanup - this is awaited so we know the result before checking
                    await _processor.Cleanup();

                    // Check for any remaining active mounts OR leftover temp directory and prompt for cleanup
                    var activeMounts = await _processor.GetActiveMountsAsync();
                    var tempDirExists = _processor.TempDirectoryExists;
                    
                    if (activeMounts.Count > 0 || tempDirExists)
                    {
                        string cleanupReason;
                        if (activeMounts.Count > 0 && tempDirExists)
                        {
                            cleanupReason = $"{activeMounts.Count} WIM mount(s) are still active and the temp directory was not fully cleaned up.";
                            _logger?.LogWarning($"Found {activeMounts.Count} active mount(s) and temp directory still exists after processing");
                        }
                        else if (activeMounts.Count > 0)
                        {
                            cleanupReason = $"{activeMounts.Count} WIM mount(s) are still active.";
                            _logger?.LogWarning($"Found {activeMounts.Count} active mount(s) after processing");
                        }
                        else
                        {
                            cleanupReason = "The temp directory was not fully cleaned up (files may be locked).";
                            _logger?.LogWarning($"Temp directory still exists after processing: {_processor.TempDirectory}");
                        }
                        
                        DispatcherQueue.TryEnqueue(async () =>
                        {
                            var result = await ThemedMessageBox.ShowAsync(this,
                                $"Processing completed, but {cleanupReason} Would you like to run Sweep Up to clean them?",
                                "Cleanup Recommended", false, "Sweep Up", "Skip");
                            if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                            {
                                // Navigate to cleanup and run sweep up
                                ShowSection("Cleanup");
                                SweepUp_Click(null!, null!);
                            }
                        });
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    _logger?.LogSuccess("=== Processing Completed Successfully ===");
                    _logger?.Save();

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        PhaseTextBlock.Text = "Progress";
                        StatusTextBlock.Text = "Completed successfully!";
                        ProgressBar.IsIndeterminate = false;
                        ProgressBar.Value = 100;
                        SetProcessingState(true, false);
                        _ = ThemedMessageBox.ShowAsync(this, $"Processing completed successfully!\n\nOutput: {outputPath}", "Success");
                    });
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogWarning("=== Processing Cancelled by User ===");
                    if (_processor != null) await _processor.Cleanup();
                    _logger?.Save();
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        PhaseTextBlock.Text = "Progress";
                        StatusTextBlock.Text = "Cancelled";
                        ProgressBar.IsIndeterminate = false;
                        ProgressBar.Value = 0;
                        SetProcessingState(true, false);
                        await ThemedMessageBox.ShowAsync(this,
                            "Processing was cancelled. If any WIM mount did not dismount, a scheduled task has been created to dismount and delete it on the next restart. You may restart your computer now or continue using the app.",
                            "Cancelled");
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Processing failed: {ex.Message}");
                    if (_processor != null) await _processor.Cleanup();
                    _logger?.Save();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        PhaseTextBlock.Text = "Progress";
                        StatusTextBlock.Text = "Processing failed!";
                        ProgressBar.IsIndeterminate = false;
                        ProgressBar.Value = 0;
                        SetProcessingState(true, false);
                        _ = ThemedMessageBox.ShowAsync(this, $"Processing failed:\n\n{ex.Message}\n\nCheck the log file for details.", "Error", true);
                    });
                }
            });
        }
        catch (Exception ex)
        {
            _ = ThemedMessageBox.ShowAsync(this, $"An error occurred while starting processing:\n\n{ex.Message}", "Error", true);
            SetProcessingState(true, false);
        }
    }

    private async void Window_Closed(object sender, WindowEventArgs args)
    {
        if (!string.IsNullOrEmpty(_mountedIsoPath) && !string.IsNullOrEmpty(_mountedIsoDrive))
        {
            try { await UnmountISO(_mountedIsoPath); } catch { }
        }
    }

    private void PopulateScratchDrives()
    {
        ScratchDriveComboBox.Items.Clear();
        ScratchDriveComboBox.Items.Add(new ScratchDriveItem { Display = "System Temp (Default)", Path = null });
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                {
                    var freeGB = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                    var totalGB = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
                    ScratchDriveComboBox.Items.Add(new ScratchDriveItem { Display = $"{drive.Name} ({label}) - {freeGB:F1} GB free of {totalGB:F1} GB", Path = drive.RootDirectory.FullName });
                }
            }
            catch { }
        }
    }

    private void SaveScratchDrivePreference()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\WIMISODriverInjector\Settings");
            if (key != null && ScratchDriveComboBox.SelectedItem is ScratchDriveItem item)
                key.SetValue("ScratchDrivePath", item.Path ?? "", RegistryValueKind.String);
        }
        catch { }
    }

    private void LoadScratchDrivePreference()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\WIMISODriverInjector\Settings");
            if (key != null)
            {
                var savedPath = key.GetValue("ScratchDrivePath") as string;
                if (!string.IsNullOrEmpty(savedPath))
                {
                    for (int i = 0; i < ScratchDriveComboBox.Items.Count; i++)
                    {
                        if (ScratchDriveComboBox.Items[i] is ScratchDriveItem item && item.Path == savedPath)
                        {
                            ScratchDriveComboBox.SelectedIndex = i;
                            return;
                        }
                    }
                }
            }
        }
        catch { }
        if (ScratchDriveComboBox.Items.Count > 0)
            ScratchDriveComboBox.SelectedIndex = 0;
    }

    private void SetProcessingState(bool enabled, bool isProcessing)
    {
        // Text boxes
        InputFileTextBox.IsEnabled = enabled;
        OutputFileTextBox.IsEnabled = enabled;
        
        // Dropdowns
        CompressionComboBox.IsEnabled = enabled;
        ScratchDriveComboBox.IsEnabled = enabled;
        
        // List controls
        DriverDirectoriesListBox.IsEnabled = enabled;
        WimFilesListBox.IsEnabled = enabled;
        VersionListBox.IsEnabled = enabled;
        
        // Browse buttons
        BrowseInputButton.IsEnabled = enabled;
        BrowseOutputButton.IsEnabled = enabled;
        
        // Driver buttons
        AddDriverButton.IsEnabled = enabled;
        RemoveDriverButton.IsEnabled = enabled;
        
        // Start/Cancel button
        StartProcessingButton.IsEnabled = true;
        StartProcessingButton.Content = isProcessing ? "Cancel" : "SQUIRT!";
    }

    private void VersionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.DataContext is VersionItem item)
            item.IsSelected = checkBox.IsChecked ?? false;
    }

    private async void SweepUp_Click(object sender, RoutedEventArgs e)
    {
        if (SweepUpButton.IsEnabled == false) return;
        SweepUpButton.IsEnabled = false;
        SweepUpProgressPanel.Visibility = Visibility.Visible;
        SweepUpProgressBar.IsIndeterminate = true;
        SweepUpStatusText.Text = "Starting...";
        try
        {
            var (needsRestart, message) = await CleanupService.SweepUpAsync(line =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    SweepUpStatusText.Text = line;
                    _logger?.LogInfo(line);
                });
            });
            if (needsRestart)
            {
                var xamlRoot = (Content as FrameworkElement)?.XamlRoot;
                var dialog = new ContentDialog
                {
                    Title = "Sweep Up",
                    Content = new TextBlock
                    {
                        Text = message,
                        TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                        FontSize = 14
                    },
                    PrimaryButtonText = "Restart Now",
                    SecondaryButtonText = "Later",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = xamlRoot
                };
                try
                {
                    var app = Microsoft.UI.Xaml.Application.Current as App;
                    if (app?.Resources != null)
                    {
                        if (app.Resources["BackgroundBrush"] is Microsoft.UI.Xaml.Media.SolidColorBrush bgBrush)
                            dialog.Background = bgBrush;
                        if (app.Resources["PrimaryTextBrush"] is Microsoft.UI.Xaml.Media.SolidColorBrush fgBrush)
                            dialog.Foreground = fgBrush;
                    }
                }
                catch { }
                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "shutdown.exe",
                        Arguments = "/r /t 5 /c \"WIM Driver Injector cleanup will complete after restart.\"",
                        UseShellExecute = true
                    });
                }
            }
            else
            {
                await ThemedMessageBox.ShowAsync(this, message, "Sweep Up");
            }
        }
        catch (Exception ex)
        {
            SweepUpStatusText.Text = "Error: " + ex.Message;
            await ThemedMessageBox.ShowAsync(this, $"Sweep Up failed:\n\n{ex.Message}", "Error", true);
        }
        finally
        {
            SweepUpProgressBar.IsIndeterminate = false;
            SweepUpProgressPanel.Visibility = Visibility.Collapsed;
            SweepUpStatusText.Text = "";
            SweepUpButton.IsEnabled = true;
        }
    }

    private sealed class ScratchDriveItem
    {
        public string Display { get; set; } = string.Empty;
        public string? Path { get; set; }
        public override string ToString() => Display;
    }

    private sealed class VersionItem
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Display { get; set; } = string.Empty;
        public bool IsSelected { get; set; }
    }
}
