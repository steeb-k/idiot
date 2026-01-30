using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WIMISODriverInjector.Core
{
    public class ImageProcessor
    {
        private readonly Logger _logger;
        private readonly string _tempDirectory;
        private readonly Action<string>? _guiLogCallback;
        private readonly Action<string>? _statusUpdateCallback;

        /// <summary>
        /// Checks the status of an ongoing DISM Capture-Image operation by monitoring the output WIM file
        /// </summary>
        public static string GetCaptureImageProgress(string outputWimPath, string sourceDir)
        {
            try
            {
                if (!File.Exists(outputWimPath))
                {
                    return "Output WIM file not found. Operation may not have started yet.";
                }

                var outputInfo = new FileInfo(outputWimPath);
                var outputSizeMB = outputInfo.Length / (1024.0 * 1024.0);
                var lastModified = outputInfo.LastWriteTime;
                var timeSinceModified = DateTime.Now - lastModified;

                // Check if DISM process is running
                var dismProcesses = Process.GetProcessesByName("dism");
                bool dismRunning = dismProcesses.Length > 0;

                // Estimate source size
                long estimatedSourceSize = 0;
                try
                {
                    var sourceDirInfo = new DirectoryInfo(sourceDir);
                    if (sourceDirInfo.Exists)
                    {
                        estimatedSourceSize = GetDirectorySizeStatic(sourceDirInfo);
                    }
                }
                catch { }

                var status = new System.Text.StringBuilder();
                status.AppendLine($"DISM Capture-Image Status:");
                status.AppendLine($"  Output WIM: {outputWimPath}");
                status.AppendLine($"  Current Size: {outputSizeMB:F2} MB");
                status.AppendLine($"  Last Modified: {lastModified:yyyy-MM-dd HH:mm:ss} ({timeSinceModified.TotalMinutes:F1} minutes ago)");
                status.AppendLine($"  DISM Process Running: {(dismRunning ? "Yes" : "No")}");

                if (estimatedSourceSize > 0)
                {
                    var estimatedFinalSize = estimatedSourceSize * 0.5; // Assume 50% compression
                    var progressPercent = Math.Min(99, (int)((outputInfo.Length / estimatedFinalSize) * 100));
                    status.AppendLine($"  Estimated Progress: ~{progressPercent}%");
                    status.AppendLine($"  Estimated Final Size: {estimatedFinalSize / (1024.0 * 1024.0):F2} MB");
                }

                if (timeSinceModified.TotalMinutes > 5 && dismRunning)
                {
                    status.AppendLine($"  WARNING: File hasn't been modified in {timeSinceModified.TotalMinutes:F1} minutes but DISM is still running.");
                    status.AppendLine($"  This may indicate the operation is stuck or processing a large file.");
                }
                else if (!dismRunning && timeSinceModified.TotalMinutes < 1)
                {
                    status.AppendLine($"  Status: Operation appears to have completed recently.");
                }
                else if (!dismRunning)
                {
                    status.AppendLine($"  Status: DISM process is not running. Operation may have completed or failed.");
                }

                return status.ToString();
            }
            catch (Exception ex)
            {
                return $"Error checking progress: {ex.Message}";
            }
        }

        private static long GetDirectorySizeStatic(DirectoryInfo dirInfo)
        {
            long size = 0;
            try
            {
                foreach (var file in dirInfo.GetFiles("*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        size += file.Length;
                    }
                    catch { }
                }
                
                foreach (var dir in dirInfo.GetDirectories("*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        size += GetDirectorySizeStatic(dir);
                    }
                    catch { }
                }
            }
            catch { }
            
            return size;
        }

        public ImageProcessor(Logger logger, Action<string>? guiLogCallback = null, Action<string>? statusUpdateCallback = null, string? scratchDirectory = null)
        {
            _logger = logger;
            _guiLogCallback = guiLogCallback;
            _statusUpdateCallback = statusUpdateCallback;

            // Use scratch directory if provided, otherwise use system temp
            if (!string.IsNullOrEmpty(scratchDirectory))
            {
                _tempDirectory = Path.Combine(scratchDirectory, "WIMDriverInjector", Guid.NewGuid().ToString());
            }
            else
            {
                _tempDirectory = Path.Combine(Path.GetTempPath(), "WIMDriverInjector", Guid.NewGuid().ToString());
            }
            
            Directory.CreateDirectory(_tempDirectory);
            _logger.LogInfo($"Using scratch directory: {_tempDirectory}");
        }

        private void LogToGui(string message)
        {
            _guiLogCallback?.Invoke(message);
        }

        private void UpdateStatus(string status)
        {
            _statusUpdateCallback?.Invoke(status);
        }

        /// <summary>
        /// Cleans up temporary directories. Should be called on failure or completion.
        /// </summary>
        public async Task Cleanup()
        {
            await CleanupTempDirectory();
        }

        private async Task CleanupTempDirectory()
        {
            if (string.IsNullOrEmpty(_tempDirectory) || !Directory.Exists(_tempDirectory))
            {
                return;
            }

            _logger.LogInfo($"Cleaning up temporary directory: {_tempDirectory}");

            // Schedule reboot cleanup so that if unmount/delete fails or app exits, next boot will purge
            await ScheduleCleanupTaskOnReboot(_tempDirectory);

            // Try multiple times as files may be locked
            int retries = 5;
            while (retries > 0)
            {
                try
                {
                    // First, try to unmount any mounted WIMs
                    var mountDirs = Directory.GetDirectories(_tempDirectory, "mount_*");
                    var unmountWarnings = new List<string>();
                    foreach (var mountDir in mountDirs)
                    {
                        try
                        {
                            _logger.LogInfo($"Attempting to unmount: {mountDir}");
                            // Use background mode for cleanup discard operations
                            await UnmountWIM(mountDir, false, cancellationToken: default, throwOnError: false, background: true);
                        }
                        catch (Exception unmountEx)
                        {
                            // Log as warning during cleanup - this is expected if already unmounted
                            var warningMsg = $"Could not unmount {mountDir}: {unmountEx.Message}";
                            _logger.LogWarning(warningMsg);
                            unmountWarnings.Add(mountDir);
                        }
                    }
                    
                    // If dismounts did not succeed, defer cleanup to next reboot instead of blocking
                    if (unmountWarnings.Count > 0)
                    {
                        _logger.LogWarning($"Some mount directories could not be unmounted. Deferring cleanup to next reboot.");
                        foreach (var mountDir in unmountWarnings)
                        {
                            _logger.LogWarning($"  - {mountDir}");
                        }
                        await Task.Delay(2000).ConfigureAwait(false); // Brief wait for any in-flight unmounts
                        await MarkForDeletionOnReboot(_tempDirectory);
                        await ScheduleCleanupTaskOnReboot(_tempDirectory);
                        _logger.LogInfo("Cleanup will complete automatically on the next reboot.");
                        break;
                    }

                    // Short wait for DISM to release file handles (avoid long blocking)
                    _logger.LogInfo("Waiting for DISM to release file handles...");
                    await Task.Delay(2000).ConfigureAwait(false);
                    
                    // Force garbage collection to help release any managed handles
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    await Task.Delay(1000).ConfigureAwait(false);
                    
                    // Try to take ownership and reset permissions on locked files
                    _logger.LogInfo("Attempting to take ownership and reset permissions on locked files...");
                    await TakeOwnershipAndResetPermissions(_tempDirectory);

                    // Try to delete individual files first to avoid permission issues
                    // Use retry logic for locked files
                    var files = Directory.GetFiles(_tempDirectory, "*", SearchOption.AllDirectories);
                    var lockedFiles = new List<string>();
                    
                    foreach (var file in files)
                    {
                        for (int fileRetry = 0; fileRetry < 3; fileRetry++)
                        {
                            try
                            {
                                if (File.Exists(file))
                                {
                                    File.SetAttributes(file, FileAttributes.Normal);
                                    
                                    // Try to open the file exclusively to check if it's locked
                                    if (IsFileLocked(file))
                                    {
                                        if (fileRetry < 2)
                                        {
                                            _logger.LogInfo($"File is locked, waiting before retry: {Path.GetFileName(file)}");
                                            await Task.Delay(1000 * (fileRetry + 1)); // Exponential backoff
                                            continue;
                                        }
                                        else
                                        {
                                            lockedFiles.Add(file);
                                            _logger.LogWarning($"File remains locked after retries: {file}");
                                            break;
                                        }
                                    }
                                    
                                    File.Delete(file);
                                    break; // Successfully deleted, exit retry loop
                                }
                                else
                                {
                                    break; // File doesn't exist, consider it "deleted"
                                }
                            }
                            catch (IOException ioEx) when (ioEx.Message.Contains("being used by another process"))
                            {
                                if (fileRetry < 2)
                                {
                                    _logger.LogInfo($"File is in use, waiting before retry: {Path.GetFileName(file)}");
                                    await Task.Delay(1000 * (fileRetry + 1));
                                    continue;
                                }
                                else
                                {
                                    lockedFiles.Add(file);
                                    _logger.LogWarning($"File remains in use after retries: {file}");
                                    break;
                                }
                            }
                            catch (UnauthorizedAccessException uaEx)
                            {
                                // Access denied - try force delete with PowerShell
                                if (fileRetry < 2)
                                {
                                    _logger.LogInfo($"Access denied for file, trying force delete: {Path.GetFileName(file)}");
                                    var forceDeleted = await ForceDeleteFile(file);
                                    if (forceDeleted)
                                    {
                                        break; // Successfully deleted, exit retry loop
                                    }
                                    await Task.Delay(1000 * (fileRetry + 1));
                                    continue;
                                }
                                else
                                {
                                    // Last retry - try force delete one more time
                                    _logger.LogWarning($"Access denied, attempting final force delete: {file}");
                                    var forceDeleted = await ForceDeleteFile(file);
                                    if (!forceDeleted)
                                    {
                                        lockedFiles.Add(file);
                                        _logger.LogWarning($"Failed to delete file after force delete attempts: {file} - {uaEx.Message}");
                                    }
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                if (fileRetry < 2)
                                {
                                    _logger.LogInfo($"Error deleting file, retrying: {Path.GetFileName(file)} - {ex.Message}");
                                    await Task.Delay(1000 * (fileRetry + 1));
                                    continue;
                                }
                                else
                                {
                                    // Last retry - try force delete
                                    if (ex.Message.Contains("access") || ex.Message.Contains("denied") || ex.Message.Contains("Access"))
                                    {
                                        _logger.LogWarning($"Access issue detected, trying force delete: {file}");
                                        var forceDeleted = await ForceDeleteFile(file);
                                        if (!forceDeleted)
                                        {
                                            lockedFiles.Add(file);
                                            _logger.LogWarning($"Failed to delete file after force delete: {file} - {ex.Message}");
                                        }
                                    }
                                    else
                                    {
                                        lockedFiles.Add(file);
                                        _logger.LogWarning($"Failed to delete file after retries: {file} - {ex.Message}");
                                    }
                                    break;
                                }
                            }
                        }
                    }
                    
                    if (lockedFiles.Count > 0)
                    {
                        _logger.LogWarning($"Some files could not be deleted (may be locked by DISM or other processes):");
                        foreach (var lockedFile in lockedFiles)
                        {
                            _logger.LogWarning($"  - {lockedFile}");
                        }
                        _logger.LogWarning("To manually delete these files, try:");
                        _logger.LogWarning("  1. Take ownership: takeown.exe /F \"<file>\" /D Y");
                        _logger.LogWarning("  2. Grant permissions: icacls.exe \"<file>\" /grant Administrators:F");
                        _logger.LogWarning("  3. Delete: Remove-Item -Path \"<file>\" -Force");
                        _logger.LogWarning("Or restart your computer - files may be released after reboot.");
                    }

                    // Delete directories (with retry logic)
                    var dirs = Directory.GetDirectories(_tempDirectory, "*", SearchOption.AllDirectories);
                    var lockedDirs = new List<string>();
                    
                    foreach (var dir in dirs.OrderByDescending(d => d.Length))
                    {
                        for (int dirRetry = 0; dirRetry < 3; dirRetry++)
                        {
                            try
                            {
                                if (Directory.Exists(dir))
                                {
                                    Directory.Delete(dir, true);
                                    break; // Successfully deleted, exit retry loop
                                }
                            }
                            catch (IOException ioEx) when (ioEx.Message.Contains("being used by another process") || 
                                                           ioEx.Message.Contains("The directory is not empty"))
                            {
                                if (dirRetry < 2)
                                {
                                    _logger.LogInfo($"Directory is in use, waiting before retry: {Path.GetFileName(dir)}");
                                    await Task.Delay(1000 * (dirRetry + 1));
                                    continue;
                                }
                                else
                                {
                                    lockedDirs.Add(dir);
                                    _logger.LogWarning($"Directory remains in use after retries: {dir}");
                                    break;
                                }
                            }
                            catch (UnauthorizedAccessException uaEx)
                            {
                                // Access denied - try force delete with PowerShell
                                if (dirRetry < 2)
                                {
                                    _logger.LogInfo($"Access denied for directory, trying force delete: {Path.GetFileName(dir)}");
                                    var forceDeleted = await ForceDeleteFile(dir);
                                    if (forceDeleted)
                                    {
                                        break; // Successfully deleted, exit retry loop
                                    }
                                    await Task.Delay(1000 * (dirRetry + 1));
                                    continue;
                                }
                                else
                                {
                                    // Last retry - try force delete one more time
                                    _logger.LogWarning($"Access denied, attempting final force delete: {dir}");
                                    var forceDeleted = await ForceDeleteFile(dir);
                                    if (!forceDeleted)
                                    {
                                        lockedDirs.Add(dir);
                                        _logger.LogWarning($"Failed to delete directory after force delete attempts: {dir} - {uaEx.Message}");
                                        // Mark for deletion on reboot as last resort
                                        await MarkForDeletionOnReboot(dir);
                                        await ScheduleCleanupTaskOnReboot(dir);
                                    }
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                if (dirRetry < 2)
                                {
                                    _logger.LogInfo($"Error deleting directory, retrying: {Path.GetFileName(dir)} - {ex.Message}");
                                    await Task.Delay(1000 * (dirRetry + 1));
                                    continue;
                                }
                                else
                                {
                                    // Last retry - try force delete if access issue
                                    if (ex.Message.Contains("access") || ex.Message.Contains("denied") || ex.Message.Contains("Access"))
                                    {
                                        _logger.LogWarning($"Access issue detected, trying force delete: {dir}");
                                        var forceDeleted = await ForceDeleteFile(dir);
                                        if (!forceDeleted)
                                        {
                                            lockedDirs.Add(dir);
                                            _logger.LogWarning($"Failed to delete directory after force delete: {dir} - {ex.Message}");
                                            // Mark for deletion on reboot as last resort
                                            await MarkForDeletionOnReboot(dir);
                                            await ScheduleCleanupTaskOnReboot(dir);
                                        }
                                    }
                                    else
                                    {
                                        lockedDirs.Add(dir);
                                        _logger.LogWarning($"Failed to delete directory after retries: {dir} - {ex.Message}");
                                    }
                                    break;
                                }
                            }
                        }
                    }
                    
                    if (lockedDirs.Count > 0)
                    {
                        _logger.LogWarning($"Some directories could not be deleted (may be locked by DISM or other processes):");
                        foreach (var lockedDir in lockedDirs)
                        {
                            _logger.LogWarning($"  - {lockedDir}");
                        }
                        _logger.LogWarning("To manually delete these directories, try:");
                        _logger.LogWarning("  1. Take ownership: takeown.exe /F \"<dir>\" /R /D Y");
                        _logger.LogWarning("  2. Grant permissions: icacls.exe \"<dir>\" /grant Administrators:F /T");
                        _logger.LogWarning("  3. Delete: Remove-Item -Path \"<dir>\" -Force -Recurse");
                        _logger.LogWarning("Or restart your computer - directories may be released after reboot.");
                    }

                    // Finally delete the temp directory itself (with retry)
                    for (int tempRetry = 0; tempRetry < 3; tempRetry++)
                    {
                        try
                        {
                            if (Directory.Exists(_tempDirectory))
                            {
                                Directory.Delete(_tempDirectory, true);
                                break; // Successfully deleted, exit retry loop
                            }
                        }
                        catch (UnauthorizedAccessException uaEx)
                        {
                            if (tempRetry < 2)
                            {
                                _logger.LogInfo($"Access denied for temp directory, trying force delete: {uaEx.Message}");
                                var forceDeleted = await ForceDeleteFile(_tempDirectory);
                                if (forceDeleted)
                                {
                                    break; // Successfully deleted, exit retry loop
                                }
                                await Task.Delay(2000 * (tempRetry + 1));
                                continue;
                            }
                            else
                            {
                                _logger.LogWarning($"Access denied for temp directory, attempting final force delete...");
                                var forceDeleted = await ForceDeleteFile(_tempDirectory);
                                if (!forceDeleted)
                                {
                                    _logger.LogWarning($"Failed to delete temp directory after force delete: {_tempDirectory} - {uaEx.Message}");
                                    _logger.LogWarning($"Temporary directory left at: {_tempDirectory}");
                                    _logger.LogWarning("Attempting to mark for deletion on reboot...");
                                    await MarkForDeletionOnReboot(_tempDirectory);
                                    await ScheduleCleanupTaskOnReboot(_tempDirectory);
                                    _logger.LogWarning("If files remain after reboot, you may need to:");
                                    _logger.LogWarning("  1. Take ownership: takeown.exe /F \"<path>\" /R /D Y");
                                    _logger.LogWarning("  2. Grant permissions: icacls.exe \"<path>\" /grant Administrators:F /T");
                                    _logger.LogWarning("  3. Delete manually: Remove-Item -Path \"<path>\" -Force -Recurse");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (tempRetry < 2)
                            {
                                _logger.LogInfo($"Error deleting temp directory, retrying: {ex.Message}");
                                await Task.Delay(2000 * (tempRetry + 1));
                                continue;
                            }
                            else
                            {
                                // Last retry - try force delete if access issue
                                if (ex.Message.Contains("access") || ex.Message.Contains("denied") || ex.Message.Contains("Access"))
                                {
                                    _logger.LogWarning($"Access issue detected, trying force delete: {_tempDirectory}");
                                    var forceDeleted = await ForceDeleteFile(_tempDirectory);
                                    if (!forceDeleted)
                                    {
                                        _logger.LogWarning($"Failed to delete temp directory after force delete: {_tempDirectory} - {ex.Message}");
                                        _logger.LogWarning($"Temporary directory left at: {_tempDirectory}");
                                        await MarkForDeletionOnReboot(_tempDirectory);
                                        await ScheduleCleanupTaskOnReboot(_tempDirectory);
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning($"Failed to delete temp directory after retries: {_tempDirectory} - {ex.Message}");
                                    _logger.LogWarning($"Temporary directory left at: {_tempDirectory}");
                                    _logger.LogWarning("You may need to manually delete this directory after DISM releases all handles.");
                                }
                            }
                        }
                    }
                    _logger.LogInfo("Temporary directory cleaned up successfully");
                    break;
                }
                catch (Exception ex)
                {
                    retries--;
                    if (retries > 0)
                    {
                        _logger.LogWarning($"Cleanup failed, retrying in 2 seconds... ({ex.Message})");
                        await Task.Delay(2000);
                    }
                    else
                    {
                        _logger.LogError($"Failed to cleanup temporary directory after retries: {ex.Message}");
                        _logger.LogWarning($"Temporary directory left at: {_tempDirectory}");
                    }
                }
            }
        }

        public async Task ProcessISO(string isoPath, string outputIsoPath, string[] driverDirectories, bool optimize, CancellationToken cancellationToken = default, Dictionary<string, List<int>>? selectedVersionsByWim = null, string? mountedDriveLetter = null)
        {
            var isoFileInfo = new FileInfo(isoPath);
            _logger.LogInfo($"=== Processing ISO File ===");
            _logger.LogInfo($"ISO Filename: {isoFileInfo.Name}");
            _logger.LogInfo($"ISO Full Path: {isoPath}");
            _logger.LogInfo($"ISO Size: {isoFileInfo.Length / (1024.0 * 1024.0):F2} MB");
            _logger.LogInfo($"Output ISO: {outputIsoPath}");

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Fail fast if we cannot create an ISO (avoid long run then silent-looking failure)
                await EnsureISOBuilderAvailableAsync();

                string extractPath;
                string? mountedPath = null;

                if (!string.IsNullOrEmpty(mountedDriveLetter))
                {
                    // Use already-mounted ISO
                    mountedPath = $"{mountedDriveLetter}:\\";
                    extractPath = Path.Combine(_tempDirectory, "iso_extract");
                    _logger.LogInfo($"=== Using Mounted ISO ===");
                    _logger.LogInfo($"Mounted Drive: {mountedPath}");
                    _logger.LogInfo($"Working Directory: {extractPath}");
                    _logger.LogInfo($"Copying files from mounted ISO...");
                    UpdateStatus("Copying files from mounted ISO...");
                    
                    // Copy all files from mounted ISO to extract path
                    Directory.CreateDirectory(extractPath);
                    await CopyDirectoryAsync(mountedPath, extractPath, cancellationToken);
                }
                else
                {
                    // Extract ISO to temporary directory
                    UpdateStatus("Extracting ISO file...");
                    extractPath = Path.Combine(_tempDirectory, "iso_extract");
                    _logger.LogInfo($"=== ISO Extraction ===");
                    _logger.LogInfo($"Extraction Directory: {extractPath}");
                    _logger.LogInfo($"Starting ISO extraction...");
                    await ExtractISO(isoPath, extractPath, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Find WIM files in the extracted ISO
                var wimFiles = Directory.GetFiles(extractPath, "*.wim", SearchOption.AllDirectories);
                
                if (wimFiles.Length == 0)
                {
                    throw new Exception("No WIM files found in ISO");
                }

                _logger.LogInfo($"Found {wimFiles.Length} WIM file(s) in ISO");

                // Process each WIM file
                int wimIndex = 1;
                foreach (var wimFile in wimFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var relativePath = Path.GetRelativePath(extractPath, wimFile);
                    var wimFileInfo = new FileInfo(wimFile);
                    _logger.LogInfo($"=== Processing WIM File {wimIndex} of {wimFiles.Length} ===");
                    _logger.LogInfo($"WIM Filename: {wimFileInfo.Name}");
                    _logger.LogInfo($"WIM Relative Path: {relativePath}");
                    _logger.LogInfo($"WIM Full Path: {wimFile}");
                    _logger.LogInfo($"WIM Size: {wimFileInfo.Length / (1024.0 * 1024.0):F2} MB");

                    // Copy WIM to writable temp location first (ISO-extracted files may be read-only)
                    var tempWimPath = Path.Combine(_tempDirectory, $"temp_{Path.GetFileName(wimFile)}");
                    _logger.LogInfo($"Copying WIM to writable location: {tempWimPath}");
                    
                    // Remove read-only from source if present
                    try
                    {
                        if ((wimFileInfo.Attributes & FileAttributes.ReadOnly) != 0)
                        {
                            _logger.LogInfo("Removing read-only attribute from source WIM...");
                            wimFileInfo.Attributes &= ~FileAttributes.ReadOnly;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Could not modify source WIM attributes: {ex.Message}");
                    }
                    
                    // Copy to temp location
                    File.Copy(wimFile, tempWimPath, true);
                    
                    // Ensure temp copy is writable
                    var tempWimInfo = new FileInfo(tempWimPath);
                    tempWimInfo.Attributes &= ~FileAttributes.ReadOnly;
                    
                    cancellationToken.ThrowIfCancellationRequested();

                    // Process to a new WIM file to avoid locking issues
                    var processedWimPath = Path.Combine(_tempDirectory, $"processed_{Path.GetFileName(wimFile)}");
                    _logger.LogInfo($"Processing WIM from writable location...");
                    
                    // Get selected versions for this WIM file
                    // boot.wim: Always process ALL indexes (no selection)
                    // install.wim: Use selected versions if provided
                    List<int>? selectedVersions = null;
                    var wimFileName = Path.GetFileName(wimFile);
                    var isBootWim = wimFileName.Equals("boot.wim", StringComparison.OrdinalIgnoreCase);
                    
                    if (isBootWim)
                    {
                        // boot.wim always processes all indexes - explicitly set to null
                        selectedVersions = null;
                        _logger.LogInfo($"boot.wim detected - will process ALL indexes");
                    }
                    else if (selectedVersionsByWim != null && selectedVersionsByWim.TryGetValue(wimFileName, out var versions))
                    {
                        // install.wim (or other WIMs) - use selected versions
                        selectedVersions = versions;
                        _logger.LogInfo($"Using selected versions for {wimFileName}: {string.Join(", ", selectedVersions)}");
                    }
                    // If no selection provided for non-boot WIMs, all indexes will be processed (selectedVersions remains null)
                    
                    await ProcessWIM(tempWimPath, processedWimPath, driverDirectories, optimize, cancellationToken, selectedVersions);
                    
                    // Replace original WIM with processed one
                    _logger.LogInfo($"Replacing original WIM with processed version...");
                    try
                    {
                        // Remove read-only from destination
                        wimFileInfo.Refresh();
                        if ((wimFileInfo.Attributes & FileAttributes.ReadOnly) != 0)
                        {
                            wimFileInfo.Attributes &= ~FileAttributes.ReadOnly;
                        }
                        
                        // Delete original and move processed file into place
                        if (File.Exists(wimFile))
                        {
                            File.Delete(wimFile);
                        }
                        File.Move(processedWimPath, wimFile);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Could not replace original WIM directly: {ex.Message}");
                        // Try deleting first, then moving
                        try
                        {
                            if (File.Exists(wimFile))
                            {
                                File.Delete(wimFile);
                            }
                            File.Move(processedWimPath, wimFile);
                        }
                        catch (Exception ex2)
                        {
                            throw new Exception($"Failed to replace original WIM file: {ex2.Message}", ex2);
                        }
                    }
                    finally
                    {
                        // Clean up temp WIM files
                        try
                        {
                            if (File.Exists(tempWimPath))
                            {
                                File.Delete(tempWimPath);
                            }
                            if (File.Exists(processedWimPath))
                            {
                                File.Delete(processedWimPath);
                            }
                        }
                        catch { }
                    }
                    
                    _logger.LogInfo($"WIM file {wimIndex} processed successfully");
                    wimIndex++;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Recreate ISO
                UpdateStatus("Creating output ISO file...");
                _logger.LogInfo($"=== ISO Creation ===");
                _logger.LogInfo($"Creating output ISO: {outputIsoPath}");
                _logger.LogInfo($"Source Directory: {extractPath}");
                await CreateISO(extractPath, outputIsoPath, cancellationToken);
                var outputInfo = new FileInfo(outputIsoPath);
                _logger.LogInfo($"Output ISO created successfully");
                _logger.LogInfo($"Output ISO Size: {outputInfo.Length / (1024.0 * 1024.0):F2} MB");

                UpdateStatus("Processing completed successfully!");
                _logger.LogSuccess("ISO processing completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"ISO processing failed: {ex.Message}");
                throw;
            }
            finally
            {
                // Run cleanup fully in background - do not block UI. User is done; unmount/delete later.
                _ = Task.Run(async () =>
                {
                    try { await CleanupTempDirectory(); }
                    catch (Exception ex) { _logger.LogWarning($"Background cleanup error: {ex.Message}"); }
                });
            }
        }

        private async Task CopyDirectoryAsync(string sourceDir, string destDir, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(destDir);
            
            var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            int totalFiles = files.Length;
            int currentFile = 0;
            
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var relativePath = Path.GetRelativePath(sourceDir, file);
                var destFile = Path.Combine(destDir, relativePath);
                var destDirPath = Path.GetDirectoryName(destFile);
                
                if (!string.IsNullOrEmpty(destDirPath))
                {
                    Directory.CreateDirectory(destDirPath);
                }
                
                File.Copy(file, destFile, true);
                
                currentFile++;
                if (currentFile % 100 == 0)
                {
                    UpdateStatus($"Copying files from mounted ISO... ({currentFile}/{totalFiles})");
                }
            }
            
            await Task.CompletedTask;
        }

        public async Task ProcessWIM(string wimPath, string outputWimPath, string[] driverDirectories, bool optimize, CancellationToken cancellationToken = default, List<int>? selectedIndexes = null)
        {
            var wimFileInfo = new FileInfo(wimPath);
            _logger.LogInfo($"=== Processing WIM File ===");
            _logger.LogInfo($"WIM Filename: {wimFileInfo.Name}");
            _logger.LogInfo($"WIM Full Path: {wimPath}");
            _logger.LogInfo($"WIM Size: {wimFileInfo.Length / (1024.0 * 1024.0):F2} MB");
            _logger.LogInfo($"Output WIM: {outputWimPath}");

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Create a new WIM file path in temp directory to avoid locking the original
                var newWimPath = Path.Combine(_tempDirectory, $"new_{Path.GetFileName(outputWimPath)}");
                if (File.Exists(newWimPath))
                {
                    File.Delete(newWimPath);
                }

                // Get WIM image indexes
                UpdateStatus("Reading WIM file structure...");
                _logger.LogInfo($"Reading WIM file structure...");
                var indexes = await GetWIMIndexes(wimPath, cancellationToken);
                _logger.LogInfo($"Found {indexes.Count} image(s) in WIM file");
                for (int i = 0; i < indexes.Count; i++)
                {
                    _logger.LogInfo($"  Image {indexes[i].Index}: {indexes[i].Name}");
                }

                // Filter indexes if specific ones are selected
                // If selectedIndexes is null or empty, process ALL indexes (used for boot.wim)
                var indexesToProcess = indexes;
                if (selectedIndexes != null && selectedIndexes.Count > 0)
                {
                    indexesToProcess = indexes.Where(idx => selectedIndexes.Contains(idx.Index)).ToList();
                    _logger.LogInfo($"Filtering to {indexesToProcess.Count} selected image(s) out of {indexes.Count} total");
                }
                else
                {
                    _logger.LogInfo($"Processing all {indexes.Count} image(s) in WIM file");
                }

                // Process each image index - export to new WIM instead of committing to original
                for (int imgIdx = 0; imgIdx < indexesToProcess.Count; imgIdx++)
                {
                    var index = indexesToProcess[imgIdx];
                    cancellationToken.ThrowIfCancellationRequested();

                    _logger.LogInfo($"Processing image index {index.Index}: {index.Name}");
                    UpdateStatus($"Processing image {index.Index} of {indexesToProcess.Count}: {index.Name}...");

                    // Mount the WIM image (read-only from original)
                    UpdateStatus($"Mounting image {index.Index}...");
                    var mountPath = Path.Combine(_tempDirectory, $"mount_{index.Index}");
                    _logger.LogInfo($"=== Mounting Image Index {index.Index} ===");
                    _logger.LogInfo($"Mount Directory: {mountPath}");
                    _logger.LogInfo($"Image Name: {index.Name}");

                    bool isMounted = false;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await MountWIM(wimPath, index.Index, mountPath, cancellationToken);
                        isMounted = true;
                        _logger.LogInfo($"Image successfully mounted to: {mountPath}");

                        cancellationToken.ThrowIfCancellationRequested();

                        // Inject drivers
                        UpdateStatus($"Injecting drivers into image {index.Index}...");
                        await InjectDrivers(mountPath, driverDirectories, cancellationToken);

                        cancellationToken.ThrowIfCancellationRequested();

                        // Export to new WIM file instead of committing to original
                        // This avoids file locking issues
                        UpdateStatus($"Exporting image {index.Index} to new WIM file...");
                        _logger.LogInfo($"=== Exporting Image {index.Index} to New WIM ===");
                        _logger.LogInfo($"Image Name: {index.Name}");
                        
                        if (imgIdx == 0)
                        {
                            // First image - create new WIM file
                            await ExportImageToNewWIM(mountPath, newWimPath, index.Name, optimize, cancellationToken);
                        }
                        else
                        {
                            // Subsequent images - append to existing WIM
                            await AppendImageToWIM(mountPath, newWimPath, index.Name, optimize, cancellationToken);
                        }

                        cancellationToken.ThrowIfCancellationRequested();

                        // Unmount without committing (we've already exported). Must wait for completion if we're
                        // about to mount the next image from the same WIM (DISM locks the WIM until unmount finishes).
                        bool moreImagesFromSameWim = imgIdx < indexesToProcess.Count - 1;
                        await UnmountWIM(mountPath, false, cancellationToken, throwOnError: true, background: !moreImagesFromSameWim);
                        isMounted = false;
                    }
                    catch (Exception ex)
                    {
                        // Try to unmount even if there was an error (only if it was actually mounted)
                        if (isMounted)
                        {
                            try
                            {
                                _logger.LogWarning($"Attempting to unmount after error...");
                                await UnmountWIM(mountPath, false, cancellationToken, throwOnError: false, background: true);
                                isMounted = false;
                            }
                            catch (Exception unmountEx)
                            {
                                _logger.LogWarning($"Failed to unmount after error: {unmountEx.Message}");
                            }
                        }

                        throw new Exception($"Failed to process image index {index.Index}: {ex.Message}", ex);
                    }
                    finally
                    {
                        // Clean up mount directory if it still exists and wasn't unmounted properly
                        if (isMounted)
                        {
                            _logger.LogWarning("Mount directory still exists after processing - attempting cleanup");
                            try
                            {
                                // Try to force unmount (don't throw on error during cleanup)
                                // Use background mode for cleanup discard operations
                                await UnmountWIM(mountPath, false, cancellationToken, throwOnError: false, background: true);
                            }
                            catch (Exception cleanupEx)
                            {
                                _logger.LogWarning($"Could not unmount during cleanup: {cleanupEx.Message}");
                                _logger.LogWarning($"Mount directory may require manual cleanup: {mountPath}");
                            }
                        }
                    }
                }

                // Ensure output directory exists
                var outputDir = Path.GetDirectoryName(outputWimPath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Now we have a new WIM file with all images processed
                // Apply optimization if requested, or just move to final location
                if (optimize)
                {
                    UpdateStatus("Optimizing and shrinking WIM file...");
                    _logger.LogInfo($"=== WIM Optimization ===");
                    _logger.LogInfo("Optimizing and shrinking WIM file...");
                    _logger.LogInfo($"Source: {newWimPath}");
                    _logger.LogInfo($"Destination: {outputWimPath}");
                    await OptimizeWIM(newWimPath, outputWimPath, cancellationToken);
                    var outputInfo = new FileInfo(outputWimPath);
                    _logger.LogInfo($"Optimization complete. Output size: {outputInfo.Length / (1024.0 * 1024.0):F2} MB");
                    
                    // Clean up temp new WIM file
                    try
                    {
                        if (File.Exists(newWimPath))
                        {
                            File.Delete(newWimPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Could not delete temp new WIM file: {ex.Message}");
                    }
                }
                else
                {
                    UpdateStatus("Moving processed WIM file to final location...");
                    _logger.LogInfo($"Moving processed WIM file to final location...");
                    
                    // Delete output file if it exists
                    if (File.Exists(outputWimPath))
                    {
                        File.Delete(outputWimPath);
                    }
                    
                    // Move the new WIM file to the output location
                    File.Move(newWimPath, outputWimPath);
                    var outputInfo = new FileInfo(outputWimPath);
                    _logger.LogInfo($"WIM file moved. Output size: {outputInfo.Length / (1024.0 * 1024.0):F2} MB");
                }

                UpdateStatus("Processing completed successfully!");
                _logger.LogSuccess("WIM processing completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"WIM processing failed: {ex.Message}");
                throw;
            }
            finally
            {
                // Run cleanup fully in background - do not block UI
                _ = Task.Run(async () =>
                {
                    try { await CleanupTempDirectory(); }
                    catch (Exception ex) { _logger.LogWarning($"Background cleanup error: {ex.Message}"); }
                });
            }
        }

        /// <summary>
        /// Runs a DISM Capture-Image process with progress monitoring
        /// </summary>
        private async Task<(string output, string error, int exitCode)> RunCaptureImageWithProgress(string fileName, string arguments, string outputWimPath, string sourceDir, CancellationToken cancellationToken = default)
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                throw new Exception($"Failed to start process: {fileName}");
            }

            // Register cancellation to kill process if cancelled
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        _logger.LogWarning($"Cancellation requested, terminating {fileName} process...");
                        process.Kill();
                    }
                }
                catch { }
            });

            // Estimate source directory size for progress calculation
            long estimatedSourceSize = 0;
            try
            {
                var sourceDirInfo = new DirectoryInfo(sourceDir);
                if (sourceDirInfo.Exists)
                {
                    estimatedSourceSize = GetDirectorySize(sourceDirInfo);
                    _logger.LogInfo($"Estimated source size: {estimatedSourceSize / (1024.0 * 1024.0):F2} MB");
                }
            }
            catch { }

            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();
            long lastFileSize = 0;
            DateTime lastUpdateTime = DateTime.Now;
            
            // Start reading both streams incrementally with progress monitoring
            var outputTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        outputBuilder.AppendLine(line);
                        
                        // Parse DISM progress output (format: "Progress: X%" or "X%")
                        var progressMatch = Regex.Match(line, @"Progress:\s*(\d+)%|(\d+)%\s*complete", RegexOptions.IgnoreCase);
                        if (progressMatch.Success)
                        {
                            // Try group 1 first (Progress: X%), then group 2 (X% complete)
                            var percentStr = progressMatch.Groups[1].Success && !string.IsNullOrEmpty(progressMatch.Groups[1].Value) 
                                ? progressMatch.Groups[1].Value 
                                : progressMatch.Groups[2].Value;
                            if (int.TryParse(percentStr, out int progressPercent))
                            {
                                UpdateStatus($"Capturing image... {progressPercent}%");
                                _logger.LogInfo($"DISM Progress: {progressPercent}%");
                            }
                        }
                        
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch { }
            }, cancellationToken);
            
            var errorTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        errorBuilder.AppendLine(line);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch { }
            }, cancellationToken);

            // Monitor file size growth as fallback progress indicator
            var progressMonitorTask = Task.Run(async () =>
            {
                while (!process.HasExited)
                {
                    try
                    {
                        if (File.Exists(outputWimPath))
                        {
                            var fileInfo = new FileInfo(outputWimPath);
                            var currentSize = fileInfo.Length;
                            
                            // Update progress every 2 seconds
                            if ((DateTime.Now - lastUpdateTime).TotalSeconds >= 2.0)
                            {
                                if (estimatedSourceSize > 0 && currentSize > 0)
                                {
                                    // Rough estimate: WIM compression means output is typically 30-70% of source
                                    // Use a conservative estimate of 50% compression
                                    var estimatedFinalSize = estimatedSourceSize * 0.5;
                                    var progressPercent = Math.Min(99, (int)((currentSize / estimatedFinalSize) * 100));
                                    
                                    if (progressPercent > 0)
                                    {
                                        UpdateStatus($"Capturing image... ~{progressPercent}% (estimated from file size)");
                                        _logger.LogInfo($"Estimated progress from file size: {progressPercent}% (Output: {currentSize / (1024.0 * 1024.0):F2} MB)");
                                    }
                                }
                                else if (currentSize > lastFileSize)
                                {
                                    // File is growing - show size
                                    var sizeMB = currentSize / (1024.0 * 1024.0);
                                    UpdateStatus($"Capturing image... ({sizeMB:F1} MB written, still processing...)");
                                    _logger.LogInfo($"Output WIM size: {sizeMB:F2} MB (growing...)");
                                }
                                
                                lastFileSize = currentSize;
                                lastUpdateTime = DateTime.Now;
                            }
                        }
                    }
                    catch { }
                    
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
            }, cancellationToken);
            
            // Wait for process to exit asynchronously
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            
            // Wait for stream reads to complete
            await Task.WhenAll(outputTask, errorTask, progressMonitorTask).ConfigureAwait(false);
            
            cancellationToken.ThrowIfCancellationRequested();

            return (outputBuilder.ToString(), errorBuilder.ToString(), process.ExitCode);
        }

        /// <summary>
        /// Gets the total size of a directory and all its contents
        /// </summary>
        private long GetDirectorySize(DirectoryInfo dirInfo)
        {
            long size = 0;
            try
            {
                // Add file sizes
                foreach (var file in dirInfo.GetFiles("*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        size += file.Length;
                    }
                    catch { }
                }
                
                // Recursively add subdirectory sizes (limit depth to avoid long delays)
                foreach (var dir in dirInfo.GetDirectories("*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        size += GetDirectorySize(dir);
                    }
                    catch { }
                }
            }
            catch { }
            
            return size;
        }

        /// <summary>
        /// Runs a process asynchronously, reading output incrementally to prevent buffer overflow and keep UI responsive
        /// </summary>
        private async Task<(string output, string error, int exitCode)> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken = default)
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                throw new Exception($"Failed to start process: {fileName}");
            }

            // Register cancellation to kill process if cancelled
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        _logger.LogWarning($"Cancellation requested, terminating {fileName} process...");
                        process.Kill();
                    }
                }
                catch { }
            });

            // Read streams incrementally to prevent buffer overflow deadlock
            // DISM can produce a lot of output, and if we don't read it, the buffer fills and DISM blocks
            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();
            
            // Start reading both streams incrementally using ReadLineAsync
            // This prevents buffer overflow and allows us to see progress
            var outputTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        outputBuilder.AppendLine(line);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch { } // Ignore errors during reading
            }, cancellationToken);
            
            var errorTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        errorBuilder.AppendLine(line);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch { } // Ignore errors during reading
            }, cancellationToken);
            
            // Wait for process to exit asynchronously - this is non-blocking
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            
            // Wait for stream reads to complete (they should finish quickly after process exits)
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            
            cancellationToken.ThrowIfCancellationRequested();

            return (outputBuilder.ToString(), errorBuilder.ToString(), process.ExitCode);
        }

        public async Task<List<WIMIndex>> GetWIMIndexes(string wimPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var indexes = new List<WIMIndex>();
            var arguments = $"/Get-WimInfo /WimFile:\"{wimPath}\"";
            _logger.LogInfo($"Executing: dism.exe {arguments}");
            LogToGui($"Executing: dism.exe {arguments}");

            var processInfo = new ProcessStartInfo
            {
                FileName = "dism.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                throw new Exception("Failed to start DISM process");
            }

            // Register cancellation to kill process if cancelled
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        _logger.LogWarning("Cancellation requested, terminating DISM Get-WimInfo process...");
                        process.Kill();
                    }
                }
                catch { }
            });

            string? currentIndex = null;
            string? currentName = null;

            while (!process.StandardOutput.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await process.StandardOutput.ReadLineAsync();
                if (line == null) break;

                // Parse DISM output
                if (line.Contains("Index :"))
                {
                    var parts = line.Split(':');
                    if (parts.Length > 1)
                    {
                        currentIndex = parts[1].Trim();
                    }
                }
                else if (line.Contains("Name :") && currentIndex != null)
                {
                    var parts = line.Split(':');
                    if (parts.Length > 1)
                    {
                        currentName = parts[1].Trim();
                        if (int.TryParse(currentIndex, out var index))
                        {
                            indexes.Add(new WIMIndex { Index = index, Name = currentName });
                            currentIndex = null;
                            currentName = null;
                        }
                    }
                }
            }

            await process.WaitForExitAsync();
            
            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                throw new Exception($"DISM failed to get WIM info: {error}");
            }

            return indexes;
        }

        private async Task MountWIM(string wimPath, int index, string mountPath, CancellationToken cancellationToken = default)
        {
            _logger.LogInfo($"Mounting WIM index {index}...");
            _logger.LogInfo($"  WIM File: {wimPath}");
            _logger.LogInfo($"  Index: {index}");
            _logger.LogInfo($"  Mount Point: {mountPath}");

            // Ensure WIM file is writable - DISM requires the WIM file itself to be writable
            try
            {
                var fileInfo = new FileInfo(wimPath);
                if (fileInfo.Exists)
                {
                    // Remove all restrictive attributes
                    fileInfo.Attributes = FileAttributes.Normal;
                    _logger.LogInfo($"Set WIM file attributes to Normal (writable)");
                    
                    // Verify file is actually writable by attempting to open it
                    try
                    {
                        using (var fs = new FileStream(wimPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                        {
                            // File is writable
                            _logger.LogInfo($"Verified WIM file is writable");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"WIM file is not writable: {ex.Message}");
                        throw new Exception($"WIM file is not writable. The file may be in use or in a read-only location. Error: {ex.Message}", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("not writable"))
                {
                    throw; // Re-throw if it's our writability check
                }
                _logger.LogWarning($"Could not modify WIM file attributes: {ex.Message}");
            }

            // Ensure mount directory exists and is empty
            if (Directory.Exists(mountPath))
            {
                try
                {
                    // Remove read-only from all files in directory first
                    var files = Directory.GetFiles(mountPath, "*", SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            fi.Attributes = FileAttributes.Normal;
                        }
                        catch { }
                    }
                    Directory.Delete(mountPath, true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Could not clean mount directory: {ex.Message}");
                }
            }
            Directory.CreateDirectory(mountPath);

            // Set directory permissions to ensure full access
            try
            {
                var dirInfo = new DirectoryInfo(mountPath);
                dirInfo.Attributes = FileAttributes.Normal;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not set mount directory attributes: {ex.Message}");
            }


            cancellationToken.ThrowIfCancellationRequested();

            var arguments = $"/Mount-Wim /WimFile:\"{wimPath}\" /Index:{index} /MountDir:\"{mountPath}\"";
            _logger.LogInfo($"Executing: dism.exe {arguments}");
            LogToGui($"Executing: dism.exe {arguments}");

            var (output, error, exitCode) = await RunProcessAsync("dism.exe", arguments, cancellationToken);

            // Log DISM output for debugging
            if (!string.IsNullOrWhiteSpace(output))
            {
                _logger.LogInfo($"DISM output: {output.Trim()}");
            }

            if (exitCode != 0)
            {
                var errorMessage = !string.IsNullOrWhiteSpace(error) ? error : !string.IsNullOrWhiteSpace(output) ? output : $"DISM exited with code {exitCode}";
                if (string.IsNullOrWhiteSpace(error) && string.IsNullOrWhiteSpace(output))
                    errorMessage += ". DISM mount usually requires running the application as Administrator.";
                _logger.LogError($"DISM mount failed: {errorMessage.Trim()}");
                throw new Exception($"DISM mount failed: {errorMessage.Trim()}");
            }

            _logger.LogSuccess($"WIM mounted successfully");
        }

        private async Task UnmountWIM(string mountPath, bool commit, CancellationToken cancellationToken = default, bool throwOnError = true, bool background = false)
        {
            _logger.LogInfo($"Unmounting WIM from {mountPath} (commit: {commit}, background: {background})");

            cancellationToken.ThrowIfCancellationRequested();

            var arguments = commit
                ? $"/Unmount-Wim /MountDir:\"{mountPath}\" /Commit"
                : $"/Unmount-Wim /MountDir:\"{mountPath}\" /Discard";

            _logger.LogInfo($"Executing: dism.exe {arguments}");
            // Do not log discard unmount to GUI when backgrounded - user should see "Done", not DISM progress
            if (!background)
                LogToGui($"Executing: dism.exe {arguments}");

            // For discard operations, run in background so the UI is never blocked
            if (!commit && background)
            {
                _logger.LogInfo("Starting discard unmount in background (non-blocking)");
                // Do not set user-facing status here; cleanup is internal. Main content scroll handles UX.
                
                // Fire-and-forget: Start the unmount process but don't wait for it
                // Use a separate cancellation token source with a timeout to prevent it from running forever
                var backgroundCts = new CancellationTokenSource(TimeSpan.FromMinutes(30)); // 30 minute max timeout
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await UnmountWIMInternal(mountPath, arguments, throwOnError, backgroundCts.Token);
                        _logger.LogInfo($"Background unmount completed for: {mountPath}");
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning($"Background unmount was cancelled or timed out for: {mountPath}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Background unmount error: {ex.Message}");
                    }
                    finally
                    {
                        backgroundCts.Dispose();
                    }
                }, CancellationToken.None);
                
                // Give it a moment to start, then continue
                await Task.Delay(100).ConfigureAwait(false);
                return;
            }

            // For commit operations or when background=false, wait for completion
            await UnmountWIMInternal(mountPath, arguments, throwOnError, cancellationToken);
        }

        private async Task UnmountWIMInternal(string mountPath, string arguments, bool throwOnError, CancellationToken cancellationToken)
        {
            try
            {
                var (output, error, exitCode) = await RunProcessAsync("dism.exe", arguments, cancellationToken);

                if (exitCode != 0)
                {
                    var errorMessage = !string.IsNullOrWhiteSpace(error) ? error : !string.IsNullOrWhiteSpace(output) ? output : $"DISM exited with code {exitCode}";
                    
                    // Check for common "already unmounted" or "not supported" errors
                    var errorLower = errorMessage.ToLower();
                    bool isNonCriticalError = errorLower.Contains("the request is not supported") ||
                                            errorLower.Contains("error: 50") ||
                                            errorLower.Contains("not mounted") ||
                                            errorLower.Contains("does not exist");
                    
                    if (isNonCriticalError)
                    {
                        // This is likely because the WIM is already unmounted or was never mounted
                        _logger.LogWarning($"DISM unmount returned non-critical error (may already be unmounted): {errorMessage.Trim()}");
                        if (!throwOnError)
                        {
                            return; // Don't throw during cleanup
                        }
                    }
                    
                    if (throwOnError)
                    {
                        _logger.LogError($"DISM unmount failed: {errorMessage.Trim()}");
                        throw new Exception($"DISM unmount failed: {errorMessage.Trim()}");
                    }
                    else
                    {
                        _logger.LogWarning($"DISM unmount failed: {errorMessage.Trim()}");
                    }
                }
                else
                {
                    _logger.LogSuccess("WIM unmounted successfully");
                    
                    // Wait a bit after successful unmount to ensure DISM releases file handles
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!throwOnError)
            {
                _logger.LogWarning($"DISM unmount error (non-critical): {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if a file is locked by attempting to open it exclusively
        /// </summary>
        private bool IsFileLocked(string filePath)
        {
            try
            {
                using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    return false; // File is not locked
                }
            }
            catch (IOException)
            {
                return true; // File is locked
            }
            catch (UnauthorizedAccessException)
            {
                return true; // File is locked or access denied
            }
        }

        /// <summary>
        /// Takes ownership of files and resets permissions using Windows tools
        /// </summary>
        private async Task TakeOwnershipAndResetPermissions(string directoryPath)
        {
            try
            {
                // Use takeown.exe to take ownership
                var takeownArgs = $"/F \"{directoryPath}\" /R /D Y";
                _logger.LogInfo($"Executing: takeown.exe {takeownArgs}");
                
                var takeownInfo = new ProcessStartInfo
                {
                    FileName = "takeown.exe",
                    Arguments = takeownArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var takeownProcess = Process.Start(takeownInfo);
                if (takeownProcess != null)
                {
                    await takeownProcess.WaitForExitAsync();
                    // Don't fail on errors - takeown may not work on all files
                }

                // Use icacls.exe to grant full control to administrators
                var icaclsArgs = $"\"{directoryPath}\" /grant Administrators:F /T /C /Q";
                _logger.LogInfo($"Executing: icacls.exe {icaclsArgs}");
                
                var icaclsInfo = new ProcessStartInfo
                {
                    FileName = "icacls.exe",
                    Arguments = icaclsArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var icaclsProcess = Process.Start(icaclsInfo);
                if (icaclsProcess != null)
                {
                    await icaclsProcess.WaitForExitAsync();
                    // Don't fail on errors - icacls may not work on all files
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not take ownership/reset permissions: {ex.Message}");
                // Don't throw - this is best-effort
            }
        }

        /// <summary>
        /// Force deletes a file using PowerShell with aggressive options
        /// </summary>
        private async Task<bool> ForceDeleteFile(string filePath)
        {
            try
            {
                // Try PowerShell Remove-Item with -Force -ErrorAction SilentlyContinue
                var script = $@"Remove-Item -Path '{filePath.Replace("'", "''")}' -Force -Recurse -ErrorAction SilentlyContinue";
                
                var processInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    return !File.Exists(filePath) && !Directory.Exists(filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"PowerShell force delete failed for {filePath}: {ex.Message}");
            }
            
            return false;
        }

        /// <summary>
        /// Marks a file or directory for deletion on the next system reboot using MoveFileEx
        /// </summary>
        private async Task MarkForDeletionOnReboot(string path)
        {
            try
            {
                // Use PowerShell to mark for deletion on reboot
                var script = $@"
                    $path = '{path.Replace("'", "''")}'
                    if (Test-Path $path) {{
                        [Microsoft.Win32.Registry]::SetValue('HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager', 'PendingFileRenameOperations', @($path), [Microsoft.Win32.RegistryValueKind]::MultiString)
                    }}
                ";
                
                var processInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    Verb = "runas" // Run as administrator
                };

                using var process = Process.Start(processInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    _logger.LogWarning($"Marked for deletion on reboot: {path}");
                    _logger.LogWarning("These files will be deleted automatically on the next system restart.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not mark for deletion on reboot: {path} - {ex.Message}");
                _logger.LogWarning($"You may need to manually delete this after restarting your computer: {path}");
            }
        }

        /// <summary>
        /// Schedules a one-time task at system startup to delete the given directory, then removes the task.
        /// Use when dismount/cleanup fails so the user does not have to manually purge.
        /// </summary>
        private async Task ScheduleCleanupTaskOnReboot(string directoryPath)
        {
            var taskName = "WIMDriverInjector_Cleanup_" + Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (taskName.Length > 200)
                taskName = "WIMDriverInjector_Cleanup_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                // Task runs at startup, deletes the folder, then deletes itself
                var deleteCmd = $"cmd /c rd /s /q \"{directoryPath}\" & schtasks /delete /tn \"{taskName}\" /f";
                var processInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/create /tn \"{taskName}\" /tr \"{deleteCmd}\" /sc onstart /ru SYSTEM /f",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var process = Process.Start(processInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    if (process.ExitCode == 0)
                        _logger.LogInfo($"Scheduled task \"{taskName}\" to remove temporary files at next startup.");
                    else
                        _logger.LogWarning($"Could not create cleanup scheduled task: {await process.StandardError.ReadToEndAsync()}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not schedule cleanup task: {ex.Message}");
            }
        }

        /// <summary>
        /// Exports a mounted image to a new WIM file (creates the WIM file)
        /// </summary>
        private async Task ExportImageToNewWIM(string mountPath, string outputWimPath, string imageName, bool optimize, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInfo($"Exporting mounted image to new WIM file: {outputWimPath}");
            _logger.LogInfo($"Image Name: {imageName}");
            
            // Use DISM /Capture-Image to create a new WIM from the mounted directory
            // Only compress if optimization is enabled
            var compressArg = optimize ? "/Compress:maximum" : "/Compress:none";
            var arguments = $"/Capture-Image /ImageFile:\"{outputWimPath}\" /CaptureDir:\"{mountPath}\" /Name:\"{imageName}\" {compressArg}";
            _logger.LogInfo($"Executing: dism.exe {arguments}");
            LogToGui($"Executing: dism.exe {arguments}");

            var (output, error, exitCode) = await RunCaptureImageWithProgress("dism.exe", arguments, outputWimPath, mountPath, cancellationToken).ConfigureAwait(false);

            if (exitCode != 0)
            {
                var errorMessage = !string.IsNullOrWhiteSpace(error) ? error : !string.IsNullOrWhiteSpace(output) ? output : $"DISM exited with code {exitCode}";
                _logger.LogError($"DISM capture failed: {errorMessage.Trim()}");
                throw new Exception($"DISM capture failed: {errorMessage.Trim()}");
            }

            _logger.LogSuccess($"Image exported to new WIM file successfully");
        }

        /// <summary>
        /// Appends a mounted image to an existing WIM file
        /// </summary>
        private async Task AppendImageToWIM(string mountPath, string wimPath, string imageName, bool optimize, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInfo($"Appending mounted image to existing WIM file: {wimPath}");
            _logger.LogInfo($"Image Name: {imageName}");
            
            // Capture to a temporary WIM file first, then export/append to the main WIM
            var tempWimPath = Path.Combine(_tempDirectory, $"temp_append_{Path.GetFileName(wimPath)}");
            if (File.Exists(tempWimPath))
            {
                File.Delete(tempWimPath);
            }
            
            // First, capture the mounted directory to a temp WIM
            var compressArg = optimize ? "/Compress:maximum" : "/Compress:none";
            var captureArgs = $"/Capture-Image /ImageFile:\"{tempWimPath}\" /CaptureDir:\"{mountPath}\" /Name:\"{imageName}\" {compressArg}";
            _logger.LogInfo($"Executing: dism.exe {captureArgs}");
            LogToGui($"Executing: dism.exe {captureArgs}");

            var (captureOutput, captureError, captureExitCode) = await RunProcessAsync("dism.exe", captureArgs, cancellationToken);

            if (captureExitCode != 0)
            {
                var errorMessage = !string.IsNullOrWhiteSpace(captureError) ? captureError : !string.IsNullOrWhiteSpace(captureOutput) ? captureOutput : $"DISM exited with code {captureExitCode}";
                _logger.LogError($"DISM capture failed: {errorMessage.Trim()}");
                throw new Exception($"DISM capture failed: {errorMessage.Trim()}");
            }

            // Now export from the temp WIM to append to the main WIM
            var arguments = $"/Export-Image /SourceImageFile:\"{tempWimPath}\" /SourceIndex:1 /DestinationImageFile:\"{wimPath}\"";
            _logger.LogInfo($"Executing: dism.exe {arguments}");
            LogToGui($"Executing: dism.exe {arguments}");

            var (exportOutput, exportError, exportExitCode) = await RunProcessAsync("dism.exe", arguments, cancellationToken);

            if (exportExitCode != 0)
            {
                var errorMessage = !string.IsNullOrWhiteSpace(exportError) ? exportError : !string.IsNullOrWhiteSpace(exportOutput) ? exportOutput : $"DISM exited with code {exportExitCode}";
                _logger.LogError($"DISM export/append failed: {errorMessage.Trim()}");
                throw new Exception($"DISM export/append failed: {errorMessage.Trim()}");
            }

            // Clean up temp WIM file
            try
            {
                if (File.Exists(tempWimPath))
                {
                    File.Delete(tempWimPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not delete temp WIM file: {ex.Message}");
            }

            _logger.LogSuccess($"Image appended to WIM file successfully");
        }

        private async Task InjectDrivers(string mountPath, string[] driverDirectories, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInfo($"=== Driver Injection ===");
            _logger.LogInfo($"Mount Point: {mountPath}");
            _logger.LogInfo($"Driver Directories: {driverDirectories.Length}");
            _logger.LogInfo("Using DISM /Add-Driver /Recurse (one call per folder for speed)");

            var validDirs = new List<string>();
            foreach (var driverDir in driverDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Directory.Exists(driverDir))
                {
                    _logger.LogWarning($"Driver directory not found: {driverDir}");
                    continue;
                }

                var infCount = Directory.GetFiles(driverDir, "*.inf", SearchOption.AllDirectories).Length;
                _logger.LogInfo($"Directory: {driverDir}");
                _logger.LogInfo($"  Found {infCount} driver file(s) (.inf)");
                if (infCount > 0)
                    validDirs.Add(driverDir);
            }

            if (validDirs.Count == 0)
            {
                _logger.LogWarning("No driver directories with .inf files found");
                return;
            }

            var successCount = 0;
            var failureCount = 0;
            int folderNum = 1;

            foreach (var driverDir in validDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(driverDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? driverDir;
                UpdateStatus($"Injecting driver folder {folderNum} of {validDirs.Count}: {dirName}...");

                try
                {
                    _logger.LogInfo($"--- Adding drivers from folder {folderNum} of {validDirs.Count} ---");
                    _logger.LogInfo($"Folder: {driverDir}");

                    cancellationToken.ThrowIfCancellationRequested();

                    // One DISM call per folder with /Recurse: DISM finds all .inf in folder and subfolders
                    var arguments = $"/Image:\"{mountPath}\" /Add-Driver /Driver:\"{driverDir}\" /Recurse";
                    _logger.LogInfo($"Executing: dism.exe {arguments}");
                    LogToGui($"Executing: dism.exe {arguments}");

                    var (output, error, exitCode) = await RunProcessAsync("dism.exe", arguments, cancellationToken);

                    if (exitCode == 0)
                    {
                        _logger.LogSuccess($"Driver folder injected successfully: {driverDir}");
                        successCount++;
                    }
                    else
                    {
                        var errMsg = !string.IsNullOrWhiteSpace(error) ? error : !string.IsNullOrWhiteSpace(output) ? output : $"Exit code {exitCode}";
                        _logger.LogDriverFailure(driverDir, errMsg);
                        failureCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDriverFailure(driverDir, ex.Message);
                    failureCount++;
                }
                folderNum++;
            }

            _logger.LogInfo($"=== Driver Injection Summary ===");
            _logger.LogInfo($"Driver folders: {validDirs.Count}");
            _logger.LogInfo($"Successful: {successCount}");
            _logger.LogInfo($"Failed: {failureCount}");
        }

        private async Task OptimizeWIM(string inputWimPath, string outputWimPath, CancellationToken cancellationToken = default)
        {
            _logger.LogInfo("Optimizing WIM file...");

            var indexes = await GetWIMIndexes(inputWimPath, cancellationToken);
            
            cancellationToken.ThrowIfCancellationRequested();

            if (indexes.Count == 1)
            {
                // Single index - simple export
                var arguments = $"/Export-Image /SourceImageFile:\"{inputWimPath}\" /SourceIndex:{indexes[0].Index} /DestinationImageFile:\"{outputWimPath}\" /Compress:maximum";
                _logger.LogInfo($"Executing: dism.exe {arguments}");
                LogToGui($"Executing: dism.exe {arguments}");

                var (output, error, exitCode) = await RunProcessAsync("dism.exe", arguments, cancellationToken);

                if (exitCode != 0)
                {
                    var errorMessage = !string.IsNullOrWhiteSpace(error) ? error : !string.IsNullOrWhiteSpace(output) ? output : $"DISM exited with code {exitCode}";
                    throw new Exception($"DISM export failed: {errorMessage.Trim()}");
                }
            }
            else
            {
                // Multiple indexes - export all to new WIM
                var tempWim = Path.Combine(_tempDirectory, "temp_optimized.wim");
                if (File.Exists(tempWim))
                {
                    File.Delete(tempWim);
                }

                for (int i = 0; i < indexes.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var index = indexes[i];
                    var targetWim = i == 0 ? tempWim : outputWimPath;

                    var arguments = $"/Export-Image /SourceImageFile:\"{inputWimPath}\" /SourceIndex:{index.Index} /DestinationImageFile:\"{targetWim}\" /Compress:maximum";
                    _logger.LogInfo($"Executing: dism.exe {arguments}");
                    LogToGui($"Executing: dism.exe {arguments}");

                    var (output, error, exitCode) = await RunProcessAsync("dism.exe", arguments, cancellationToken);

                    if (exitCode != 0)
                    {
                        var errorMessage = !string.IsNullOrWhiteSpace(error) ? error : !string.IsNullOrWhiteSpace(output) ? output : $"DISM exited with code {exitCode}";
                        throw new Exception($"DISM export failed: {errorMessage.Trim()}");
                    }
                }

                // Move first export to final location
                if (File.Exists(tempWim))
                {
                    if (File.Exists(outputWimPath))
                    {
                        File.Delete(outputWimPath);
                    }
                    File.Move(tempWim, outputWimPath);
                }
                return;
            }

            _logger.LogSuccess("WIM optimization completed");
        }

        private async Task ExtractISO(string isoPath, string extractPath, CancellationToken cancellationToken = default)
        {
            _logger.LogInfo("Extracting ISO file...");
            
            cancellationToken.ThrowIfCancellationRequested();

            // Use PowerShell to extract ISO (available in Windows 10+ and PE)
            var script = $@"
                $iso = Mount-DiskImage -ImagePath '{isoPath}' -PassThru
                $driveLetter = ($iso | Get-Volume).DriveLetter
                $sourcePath = $driveLetter + ':\'
                Copy-Item -Path $sourcePath -Destination '{extractPath}' -Recurse -Force
                Dismount-DiskImage -ImagePath '{isoPath}'
            ";

            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInfo($"Executing PowerShell: Mount-DiskImage, Copy-Item, Dismount-DiskImage for ISO extraction");
            LogToGui($"Executing PowerShell: Mount-DiskImage, Copy-Item, Dismount-DiskImage for ISO extraction");

            var processInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                throw new Exception("Failed to start PowerShell extraction process");
            }

            // Register cancellation to kill process if cancelled
            using var extractReg = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        _logger.LogWarning("Cancellation requested, terminating PowerShell extraction process...");
                        process.Kill();
                    }
                }
                catch { }
            });

            await process.WaitForExitAsync();
            
            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                throw new Exception($"ISO extraction failed: {error}");
            }

            _logger.LogSuccess("ISO extracted successfully");
        }

        /// <summary>
        /// Ensures oscdimg or PowerShell New-IsoFile is available before starting ISO processing.
        /// Call at the start of ProcessISO to fail fast with a clear message.
        /// </summary>
        private async Task EnsureISOBuilderAvailableAsync()
        {
            if (FindOSCDIMG() != null)
                return;
            var checkScript = "Get-Command New-IsoFile -ErrorAction SilentlyContinue";
            var checkProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{checkScript}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (checkProcess != null)
            {
                await checkProcess.WaitForExitAsync();
                var output = await checkProcess.StandardOutput.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(output))
                    return;
            }
            var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            throw new Exception(
                "ISO creation requires oscdimg.exe. Place oscdimg.exe in the application folder or in a \"Tools\" subfolder. " +
                "You can obtain it from the Windows ADK (Assessment and Deployment Kit). " +
                $"Application folder: {appDir}");
        }

        private async Task CreateISO(string sourcePath, string outputIsoPath, CancellationToken cancellationToken = default)
        {
            _logger.LogInfo("Creating ISO file...");

            // Use oscdimg (bundled or Windows ADK) first, then fall back to PowerShell New-IsoFile

            var oscdimgPath = FindOSCDIMG();
            if (oscdimgPath != null)
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = oscdimgPath,
                    Arguments = $"-m -o -u2 -udfver102 -bootdata:2#p0,e,b\"{sourcePath}\\boot\\etfsboot.com\"#pEF,e,b\"{sourcePath}\\efi\\Microsoft\\boot\\efisys.bin\" \"{sourcePath}\" \"{outputIsoPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    if (process.ExitCode == 0)
                    {
                        _logger.LogSuccess("ISO created successfully using oscdimg");
                        return;
                    }
                }
            }

            // Fallback to PowerShell (Windows 10 1803+)
            _logger.LogInfo("Using PowerShell to create ISO...");
            var script = $@"
                $source = '{sourcePath}'
                $destination = '{outputIsoPath}'
                New-IsoFile -SourcePath $source -DestinationPath $destination
            ";

            // Check if New-IsoFile cmdlet is available (Windows 10 1803+)
            var checkScript = "Get-Command New-IsoFile -ErrorAction SilentlyContinue";
            var checkProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{checkScript}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });

            if (checkProcess != null)
            {
                await checkProcess.WaitForExitAsync();
                var output = await checkProcess.StandardOutput.ReadToEndAsync();
                
                if (string.IsNullOrWhiteSpace(output))
                {
                    var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    throw new Exception(
                        "ISO creation requires oscdimg.exe. Place oscdimg.exe in the application folder or in a \"Tools\" subfolder. " +
                        "You can obtain it from the Windows ADK (Assessment and Deployment Kit). " +
                        $"Application folder: {appDir}");
                }
            }

            _logger.LogInfo($"Executing PowerShell: New-IsoFile -SourcePath \"{sourcePath}\" -DestinationPath \"{outputIsoPath}\"");

            var isoProcessInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var isoProcess = Process.Start(isoProcessInfo);
            if (isoProcess == null)
            {
                throw new Exception("Failed to start PowerShell ISO creation process");
            }

            await isoProcess.WaitForExitAsync();

            if (isoProcess.ExitCode != 0)
            {
                var error = await isoProcess.StandardError.ReadToEndAsync();
                throw new Exception($"ISO creation failed: {error}");
            }

            _logger.LogSuccess("ISO created successfully");
        }

        private string? FindOSCDIMG()
        {
            // Prefer oscdimg packaged with the application (same folder or Tools subfolder)
            var appBase = AppContext.BaseDirectory;
            var bundledPaths = new[]
            {
                Path.Combine(appBase, "oscdimg.exe"),
                Path.Combine(appBase, "Tools", "oscdimg.exe"),
            };
            foreach (var path in bundledPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            // Fall back to Windows ADK install locations
            var adkPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Kits", "10", "Assessment and Deployment Kit", "Deployment Tools", "amd64", "Oscdimg", "oscdimg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Assessment and Deployment Kit", "Deployment Tools", "amd64", "Oscdimg", "oscdimg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Kits", "10", "Assessment and Deployment Kit", "Deployment Tools", "x86", "Oscdimg", "oscdimg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Assessment and Deployment Kit", "Deployment Tools", "x86", "Oscdimg", "oscdimg.exe"),
            };
            foreach (var path in adkPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }
    }

    public class WIMIndex
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
