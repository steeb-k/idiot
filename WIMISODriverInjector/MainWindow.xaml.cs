using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using WIMISODriverInjector.Core;

namespace WIMISODriverInjector
{
    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        
        private readonly ObservableCollection<string> _driverDirectories = new();
        private readonly ObservableCollection<string> _wimFiles = new();
        private readonly ObservableCollection<VersionItem> _versionItems = new();
        private readonly ObservableCollection<string> _logFiles = new();
        
        private Logger? _logger;
        private ImageProcessor? _processor;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _processingTask;
        
        private string? _selectedInstallWimPath;
        private List<WIMIndex>? _installWimIndexes;
        private string? _mountedIsoPath;
        private string? _mountedIsoDrive;

        public MainWindow()
        {
            try
            {
                InitializeComponent();
                
                // Set window titlebar to dark mode if system is in dark mode
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                    if (key != null)
                    {
                        var appsUseLightTheme = key.GetValue("AppsUseLightTheme");
                        if (appsUseLightTheme is int value && value == 0) // 0 = dark mode
                        {
                            var helper = new WindowInteropHelper(this);
                            helper.EnsureHandle();
                            var hwnd = helper.Handle;
                            if (hwnd != IntPtr.Zero)
                            {
                                const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
                                int useDarkMode = 1;
                                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
                            }
                        }
                    }
                }
                catch { }
                
                // Initialize UI
                PopulateScratchDrives();
                LoadScratchDrivePreference();
                DriverDirectoriesListBox.ItemsSource = _driverDirectories;
                WimFilesListBox.ItemsSource = _wimFiles;
                VersionListBox.ItemsSource = _versionItems;
                LogFilesListBox.ItemsSource = _logFiles;
                
                // Save scratch drive preference when it changes
                ScratchDriveComboBox.SelectionChanged += (s, e) => SaveScratchDrivePreference();
                
                // Set default log file with timestamp in logs subfolder
                var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(logsDir); // Ensure logs directory exists
                var logFileName = $"injection-log-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt";
                LogFileTextBox.Text = Path.Combine(logsDir, logFileName);
                
                // Show Image Selection by default and set it as active
                ImageSelectionButton.Tag = "Active";
                ShowSection("Image");
                
                // Populate log files
                RefreshLogFiles();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing MainWindow:\n\n{ex.Message}\n\n{ex.StackTrace}", 
                    "Initialization Error", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Error);
                throw;
            }
        }

        private void NavigationButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                // Get the section from the button's original Tag (before it might be set to "Active")
                string section = button.Tag?.ToString() ?? "";
                if (section == "Active")
                {
                    // If already active, try to get section from name
                    if (button == ImageSelectionButton) section = "Image";
                    else if (button == DriverSelectionButton) section = "Driver";
                    else if (button == OptionsButton) section = "Options";
                    else if (button == LogsButton) section = "Logs";
                    else if (button == AboutButton) section = "About";
                }
                
                if (!string.IsNullOrEmpty(section))
                {
                    ShowSection(section);
                }
            }
        }

        private void ShowSection(string section)
        {
            // Clear active state from all navigation buttons
            ImageSelectionButton.Tag = "Image";
            DriverSelectionButton.Tag = "Driver";
            OptionsButton.Tag = "Options";
            LogsButton.Tag = "Logs";
            AboutButton.Tag = "About";
            
            // Hide all panels
            ImageSelectionPanel.Visibility = Visibility.Collapsed;
            DriverSelectionPanel.Visibility = Visibility.Collapsed;
            OptionsPanel.Visibility = Visibility.Collapsed;
            LogsPanel.Visibility = Visibility.Collapsed;
            AboutPanel.Visibility = Visibility.Collapsed;

            // Show selected panel and set active state
            switch (section)
            {
                case "Image":
                    ImageSelectionPanel.Visibility = Visibility.Visible;
                    ImageSelectionButton.Tag = "Active";
                    break;
                case "Driver":
                    try
                    {
                        // Ensure the list box is properly initialized BEFORE making panel visible
                        if (DriverDirectoriesListBox.ItemsSource == null)
                        {
                            DriverDirectoriesListBox.ItemsSource = _driverDirectories;
                        }
                        DriverSelectionPanel.Visibility = Visibility.Visible;
                        DriverSelectionButton.Tag = "Active";
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
                            Directory.CreateDirectory(logsDir);
                            File.AppendAllText(Path.Combine(logsDir, "startup-log.txt"), 
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR in ShowSection(Driver): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
                        }
                        catch { }
                        MessageBox.Show($"Error showing Driver Selection section:\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}", 
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    break;
                case "Options":
                    OptionsPanel.Visibility = Visibility.Visible;
                    OptionsButton.Tag = "Active";
                    break;
                case "Logs":
                    LogsPanel.Visibility = Visibility.Visible;
                    LogsButton.Tag = "Active";
                    RefreshLogFiles();
                    break;
                case "About":
                    AboutPanel.Visibility = Visibility.Visible;
                    AboutButton.Tag = "Active";
                    break;
            }
        }

        private async void BrowseInputFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "ISO/WIM Files|*.iso;*.wim|ISO Files|*.iso|WIM Files|*.wim|All Files|*.*",
                Title = "Select Input ISO or WIM File"
            };

            if (dialog.ShowDialog() == true)
            {
                // Unmount any previously mounted ISO
                if (!string.IsNullOrEmpty(_mountedIsoPath) && !string.IsNullOrEmpty(_mountedIsoDrive))
                {
                    try
                    {
                        await UnmountISO(_mountedIsoPath);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Warning: Failed to unmount previous ISO: {ex.Message}");
                    }
                    _mountedIsoPath = null;
                    _mountedIsoDrive = null;
                }
                
                InputFileTextBox.Text = dialog.FileName;
                
                // Auto-suggest output filename
                if (string.IsNullOrWhiteSpace(OutputFileTextBox.Text))
                {
                    var inputFile = new FileInfo(dialog.FileName);
                    var outputName = Path.ChangeExtension(inputFile.Name, null) + "_injected" + inputFile.Extension;
                    OutputFileTextBox.Text = Path.Combine(inputFile.DirectoryName ?? "", outputName);
                }

                // If ISO, mount it automatically
                if (dialog.FileName.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    await ExtractIsoFile(dialog.FileName);
                }
                else if (dialog.FileName.EndsWith(".wim", StringComparison.OrdinalIgnoreCase))
                {
                    // For WIM files, just show it in the list
                    _wimFiles.Clear();
                    _wimFiles.Add(dialog.FileName);
                    WimFilesPanel.Visibility = Visibility.Visible;
                    
                    // Check if it's install.wim and load versions
                    if (Path.GetFileName(dialog.FileName).Equals("install.wim", StringComparison.OrdinalIgnoreCase))
                    {
                        await LoadWimVersions(dialog.FileName);
                    }
                }
            }
        }

        private async Task ExtractIsoFile(string isoPath)
        {
            try
            {
                // Unmount any previously mounted ISO
                if (!string.IsNullOrEmpty(_mountedIsoPath) && !string.IsNullOrEmpty(_mountedIsoDrive))
                {
                    await UnmountISO(_mountedIsoPath);
                }
                
                // Show mounting progress
                ExtractionProgressPanel.Visibility = Visibility.Visible;
                WimFilesPanel.Visibility = Visibility.Collapsed;
                ExtractionStatusText.Text = "Mounting ISO file...";
                
                // Disable UI during mounting
                InputFileTextBox.IsEnabled = false;
                
                // Mount ISO and get drive letter
                _mountedIsoDrive = await MountISO(isoPath);
                _mountedIsoPath = isoPath;
                
                if (string.IsNullOrEmpty(_mountedIsoDrive))
                {
                    throw new Exception("Failed to mount ISO - no drive letter returned");
                }
                
                ExtractionStatusText.Text = "Scanning for WIM files...";
                
                // Find WIM files in mounted ISO
                var mountedPath = $"{_mountedIsoDrive}:\\";
                var wimFiles = Directory.GetFiles(mountedPath, "*.wim", SearchOption.AllDirectories);
                _wimFiles.Clear();
                foreach (var wimFile in wimFiles)
                {
                    var relativePath = Path.GetRelativePath(mountedPath, wimFile);
                    _wimFiles.Add(relativePath);
                }
                
                ExtractionProgressPanel.Visibility = Visibility.Collapsed;
                WimFilesPanel.Visibility = Visibility.Visible;
                InputFileTextBox.IsEnabled = true;
                
                // Check for install.wim and load versions
                var installWim = wimFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("install.wim", StringComparison.OrdinalIgnoreCase));
                if (installWim != null)
                {
                    _selectedInstallWimPath = installWim;
                    await LoadWimVersions(installWim);
                    // Show version selection in Image Selection section
                    VersionSelectionPanel.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                ExtractionProgressPanel.Visibility = Visibility.Collapsed;
                InputFileTextBox.IsEnabled = true;
                
                // Try to unmount on error
                if (!string.IsNullOrEmpty(_mountedIsoPath))
                {
                    try
                    {
                        await UnmountISO(_mountedIsoPath);
                    }
                    catch { }
                    _mountedIsoPath = null;
                    _mountedIsoDrive = null;
                }
                
                MessageBox.Show($"Failed to mount ISO:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task<string> MountISO(string isoPath)
        {
            // Use PowerShell to mount ISO and return drive letter
            var script = $@"
                $iso = Mount-DiskImage -ImagePath '{isoPath}' -PassThru
                $driveLetter = ($iso | Get-Volume).DriveLetter
                Write-Output $driveLetter
            ";

            var processInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(processInfo);
            if (process == null)
            {
                throw new Exception("Failed to start PowerShell mount process");
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                throw new Exception($"ISO mount failed: {error}");
            }

            var driveLetter = output.Trim();
            if (string.IsNullOrEmpty(driveLetter))
            {
                throw new Exception("ISO mounted but no drive letter returned");
            }

            return driveLetter;
        }

        private async Task UnmountISO(string isoPath)
        {
            // Use PowerShell to unmount ISO
            var script = $@"
                Dismount-DiskImage -ImagePath '{isoPath}'
            ";

            var processInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(processInfo);
            if (process == null)
            {
                throw new Exception("Failed to start PowerShell unmount process");
            }

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                // Don't throw on unmount errors - ISO might already be unmounted
                System.Diagnostics.Debug.WriteLine($"ISO unmount warning: {error}");
            }
        }

        private async Task LoadWimVersions(string wimPath)
        {
            try
            {
                // Create temporary logger
                var tempLogPath = Path.Combine(Path.GetTempPath(), "wim-info-log.txt");
                var tempLogger = new Logger(tempLogPath);
                
                // Create ImageProcessor to get WIM indexes
                var processor = new ImageProcessor(tempLogger, null, null, null);
                _installWimIndexes = await processor.GetWIMIndexes(wimPath);
                
                // Populate version list
                _versionItems.Clear();
                foreach (var index in _installWimIndexes)
                {
                    _versionItems.Add(new VersionItem
                    {
                        Index = index.Index,
                        Name = index.Name,
                        Display = $"Index {index.Index}: {index.Name}",
                        IsSelected = true // Default to all selected
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load WIM versions:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BrowseOutputFile_Click(object sender, RoutedEventArgs e)
        {
            var inputPath = InputFileTextBox.Text;
            var isISO = !string.IsNullOrEmpty(inputPath) && inputPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
            var isWIM = !string.IsNullOrEmpty(inputPath) && inputPath.EndsWith(".wim", StringComparison.OrdinalIgnoreCase);

            var dialog = new SaveFileDialog
            {
                Filter = isISO ? "ISO Files|*.iso|All Files|*.*" : isWIM ? "WIM Files|*.wim|All Files|*.*" : "All Files|*.*",
                Title = "Select Output File Location",
                FileName = OutputFileTextBox.Text
            };

            if (dialog.ShowDialog() == true)
            {
                OutputFileTextBox.Text = dialog.FileName;
            }
        }

        private void AddDriverDirectory_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select directory containing driver files (.inf)"
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var path = dialog.SelectedPath;
                if (!_driverDirectories.Contains(path))
                {
                    _driverDirectories.Add(path);
                }
            }
        }

        private void RemoveDriverDirectory_Click(object sender, RoutedEventArgs e)
        {
            if (DriverDirectoriesListBox.SelectedItem is string selected)
            {
                _driverDirectories.Remove(selected);
            }
        }

        private void BrowseLogFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Text Files|*.txt|All Files|*.*",
                Title = "Select Log File Location",
                FileName = LogFileTextBox.Text
            };

            if (dialog.ShowDialog() == true)
            {
                LogFileTextBox.Text = dialog.FileName;
            }
        }

        private void LogFilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LogFilesListBox.SelectedItem is string logFile && File.Exists(logFile))
            {
                try
                {
                    LogContentTextBlock.Text = File.ReadAllText(logFile);
                }
                catch (Exception ex)
                {
                    LogContentTextBlock.Text = $"Error reading log file: {ex.Message}";
                }
            }
        }

        private void RefreshLogFiles()
        {
            _logFiles.Clear();
            var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
            
            // Look in logs folder if it exists, otherwise fall back to base directory for backward compatibility
            var logDir = Directory.Exists(logsDir) ? logsDir : AppContext.BaseDirectory;
            
            var logFiles = Directory.GetFiles(logDir, "injection-log-*.txt")
                .Concat(Directory.GetFiles(logDir, "startup-log*.txt"))
                .OrderByDescending(f => new FileInfo(f).LastWriteTime);
            
            foreach (var logFile in logFiles)
            {
                _logFiles.Add(logFile);
            }
        }

        private void StartProcessing_Click(object sender, RoutedEventArgs e)
        {
            // If processing is active, cancel it
            if (_processingTask != null && !_processingTask.IsCompleted)
            {
                _logger?.LogInfo("Cancellation requested by user...");
                _cancellationTokenSource?.Cancel();
                return;
            }

            // Validate inputs
            if (string.IsNullOrWhiteSpace(InputFileTextBox.Text))
            {
                MessageBox.Show("Please select an input file.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!File.Exists(InputFileTextBox.Text))
            {
                MessageBox.Show("Input file does not exist.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(OutputFileTextBox.Text))
            {
                MessageBox.Show("Please specify an output file.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_driverDirectories.Count == 0)
            {
                MessageBox.Show("Please add at least one driver directory.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Get selected versions for install.wim
            var selectedVersions = _versionItems.Where(v => v.IsSelected).Select(v => v.Index).ToList();
            if (_selectedInstallWimPath != null && selectedVersions.Count == 0)
            {
                MessageBox.Show("Please select at least one Windows version to include.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Create cancellation token source
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            // Disable UI during processing
            SetProcessingState(false, isProcessing: true);

            try
            {
                // Update log file name with date if using default
                if (LogFileTextBox.Text == "injection-log.txt" || !Path.IsPathRooted(LogFileTextBox.Text))
                {
                    var logFileName = $"injection-log-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt";
                    LogFileTextBox.Text = Path.Combine(AppContext.BaseDirectory, logFileName);
                }

                _logger = new Logger(LogFileTextBox.Text);
                _logger.LogInfo("=== Processing Started ===");
                _logger.LogInfo($"Input: {InputFileTextBox.Text}");
                _logger.LogInfo($"Output: {OutputFileTextBox.Text}");

                // Create callbacks - use InvokeAsync to keep UI responsive
                Action<string> guiLogCallback = (message) =>
                {
                    Dispatcher.InvokeAsync(() => StatusTextBlock.Text = message);
                };
                
                Action<string> statusUpdateCallback = (status) =>
                {
                    Dispatcher.InvokeAsync(() => StatusTextBlock.Text = status);
                };

                // Get selected scratch drive
                string? scratchDirectory = null;
                if (ScratchDriveComboBox.SelectedItem is ScratchDriveItem selectedItem)
                {
                    scratchDirectory = selectedItem.Path;
                }

                _processor = new ImageProcessor(_logger, guiLogCallback, statusUpdateCallback, scratchDirectory);

                var inputPath = InputFileTextBox.Text;
                var outputPath = OutputFileTextBox.Text;
                var driverDirs = _driverDirectories.ToArray();
                var optimize = OptimizeCheckBox.IsChecked ?? false;

                StatusTextBlock.Text = "Processing...";
                ProgressBar.IsIndeterminate = true;

                // Process in background
                _processingTask = Task.Run(async () =>
                {
                    try
                    {
                        bool isISO = inputPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
                        
                        // Get selected versions for install.wim
                        List<int>? selectedVersions = null;
                        if (_selectedInstallWimPath != null && _versionItems.Count > 0)
                        {
                            selectedVersions = _versionItems.Where(v => v.IsSelected).Select(v => v.Index).ToList();
                        }
                        
                        if (isISO)
                        {
                            // Build selected versions dictionary for ProcessISO
                            Dictionary<string, List<int>>? selectedVersionsByWim = null;
                            if (_selectedInstallWimPath != null && _versionItems.Count > 0)
                            {
                                selectedVersionsByWim = new Dictionary<string, List<int>>();
                                var installWimFileName = Path.GetFileName(_selectedInstallWimPath);
                                var selectedVersionsList = _versionItems.Where(v => v.IsSelected).Select(v => v.Index).ToList();
                                if (selectedVersionsList.Count > 0)
                                {
                                    selectedVersionsByWim[installWimFileName] = selectedVersionsList;
                                }
                            }
                            
                            // Pass mounted drive letter if available
                            string? mountedDrive = null;
                            if (!string.IsNullOrEmpty(_mountedIsoDrive) && inputPath.Equals(_mountedIsoPath, StringComparison.OrdinalIgnoreCase))
                            {
                                mountedDrive = _mountedIsoDrive;
                                _logger?.LogInfo($"Using already-mounted ISO drive: {mountedDrive}:\\");
                            }
                            
                            await _processor.ProcessISO(inputPath, outputPath, driverDirs, optimize, cancellationToken, selectedVersionsByWim, mountedDrive);
                        }
                        else
                        {
                            // For direct WIM files, use selected versions
                            // boot.wim always processes all indexes (ignore selection)
                            var wimFileName = Path.GetFileName(inputPath);
                            if (wimFileName.Equals("boot.wim", StringComparison.OrdinalIgnoreCase))
                            {
                                selectedVersions = null; // Force all indexes for boot.wim
                                _logger?.LogInfo("boot.wim detected - will process ALL indexes");
                            }
                            
                            await _processor.ProcessWIM(inputPath, outputPath, driverDirs, optimize, cancellationToken, selectedVersions);
                        }

                        cancellationToken.ThrowIfCancellationRequested();

                        _logger?.LogSuccess("=== Processing Completed Successfully ===");
                        _logger?.Save();

                        Dispatcher.Invoke(() =>
                        {
                            StatusTextBlock.Text = "Completed successfully!";
                            ProgressBar.IsIndeterminate = false;
                            ProgressBar.Value = 100;
                            SetProcessingState(true, isProcessing: false);
                            MessageBox.Show($"Processing completed successfully!\n\nOutput: {outputPath}", 
                                "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        _logger?.LogWarning("=== Processing Cancelled by User ===");
                        if (_processor != null)
                        {
                            await _processor.Cleanup();
                        }
                        _logger?.Save();

                        Dispatcher.Invoke(() =>
                        {
                            StatusTextBlock.Text = "Cancelled";
                            ProgressBar.IsIndeterminate = false;
                            ProgressBar.Value = 0;
                            SetProcessingState(true, isProcessing: false);
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"Processing failed: {ex.Message}");
                        if (_processor != null)
                        {
                            await _processor.Cleanup();
                        }
                        _logger?.Save();

                        Dispatcher.Invoke(() =>
                        {
                            StatusTextBlock.Text = "Processing failed!";
                            ProgressBar.IsIndeterminate = false;
                            ProgressBar.Value = 0;
                            SetProcessingState(true, isProcessing: false);
                            MessageBox.Show($"Processing failed:\n\n{ex.Message}\n\nCheck the log file for details.", 
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred while starting processing:\n\n{ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetProcessingState(true, isProcessing: false);
            }
        }

        private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Unmount any mounted ISO before closing
            if (!string.IsNullOrEmpty(_mountedIsoPath) && !string.IsNullOrEmpty(_mountedIsoDrive))
            {
                try
                {
                    await UnmountISO(_mountedIsoPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Warning: Failed to unmount ISO on window close: {ex.Message}");
                }
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
                        var freeSpaceGB = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                        var totalSpaceGB = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
                        var display = $"{drive.Name} ({drive.VolumeLabel}) - {freeSpaceGB:F1} GB free of {totalSpaceGB:F1} GB";
                        ScratchDriveComboBox.Items.Add(new ScratchDriveItem { Display = display, Path = drive.RootDirectory.FullName });
                    }
                }
                catch { }
            }
        }

        private void SaveScratchDrivePreference()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\WIMISODriverInjector\Settings");
                if (key != null)
                {
                    if (ScratchDriveComboBox.SelectedItem is ScratchDriveItem selectedItem)
                    {
                        key.SetValue("ScratchDrivePath", selectedItem.Path ?? "", Microsoft.Win32.RegistryValueKind.String);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save scratch drive preference: {ex.Message}");
            }
        }

        private void LoadScratchDrivePreference()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\WIMISODriverInjector\Settings");
                if (key != null)
                {
                    var savedPath = key.GetValue("ScratchDrivePath") as string;
                    if (!string.IsNullOrEmpty(savedPath))
                    {
                        // Find the matching item in the combo box
                        foreach (ScratchDriveItem item in ScratchDriveComboBox.Items)
                        {
                            if (item.Path == savedPath)
                            {
                                ScratchDriveComboBox.SelectedItem = item;
                                return;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load scratch drive preference: {ex.Message}");
            }
            
            // Default to first item if no preference found or loading failed
            if (ScratchDriveComboBox.Items.Count > 0)
            {
                ScratchDriveComboBox.SelectedIndex = 0;
            }
        }

        private void SetProcessingState(bool enabled, bool isProcessing = false)
        {
            InputFileTextBox.IsEnabled = enabled;
            OutputFileTextBox.IsEnabled = enabled;
            DriverDirectoriesListBox.IsEnabled = enabled;
            OptimizeCheckBox.IsEnabled = enabled;
            LogFileTextBox.IsEnabled = enabled;
            StartProcessingButton.IsEnabled = true;
            
            if (isProcessing)
            {
                StartProcessingButton.Content = "Cancel";
            }
            else
            {
                StartProcessingButton.Content = "Start Processing";
            }
        }

        private class ScratchDriveItem
        {
            public string Display { get; set; } = string.Empty;
            public string? Path { get; set; }
            public override string ToString() => Display;
        }

        private void VersionCheckBox_Click(object sender, RoutedEventArgs e)
        {
            // This ensures the binding updates properly
            if (sender is CheckBox checkBox && checkBox.DataContext is VersionItem item)
            {
                item.IsSelected = checkBox.IsChecked ?? false;
            }
        }

        private class VersionItem
        {
            public int Index { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Display { get; set; } = string.Empty;
            public bool IsSelected { get; set; }
        }
    }
}
