using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WIMISODriverInjector.Core;

/// <summary>
/// Scans local drives and temp for WIMDriverInjector folders, dismounts WIM mounts, takes ownership, and deletes.
/// If dismount fails, schedules a task to clean on next restart.
/// </summary>
public static class CleanupService
{
    /// <summary>
    /// Sweep all local drives and system temp for WIMDriverInjector folders.
    /// Comprehensive cleanup order:
    /// 1. Attempt to dismount all WIM directories
    /// 2. Run dism.exe /Cleanup-Wim
    /// 3. Attempt to take ownership and permissions on folders
    /// 4. Attempt to delete them
    /// 5. Schedule a task for any remaining pieces
    /// Returns (needsRestart, summaryMessage).
    /// </summary>
    public static async Task<(bool needsRestart, string message)> SweepUpAsync(Action<string>? log = null)
    {
        void Log(string msg)
        {
            log?.Invoke(msg);
        }

        Log("Scanning local drives and system temp...");
        var roots = new List<string>();
        try
        {
            roots.Add(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        catch { }

        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                try
                {
                    roots.Add(drive.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
                catch { }
            }
        }
        catch { }

        var wimDriverInjectorDirs = new List<string>();
        foreach (var root in roots.Distinct())
        {
            try
            {
                var candidate = Path.Combine(root, "WIMDriverInjector");
                if (Directory.Exists(candidate))
                    wimDriverInjectorDirs.Add(candidate);
            }
            catch { }
        }

        if (wimDriverInjectorDirs.Count == 0)
        {
            Log("No WIMDriverInjector folders found.");
            return (false, "No WIMDriverInjector folders found on local drives or in the system temp folder.");
        }

        Log($"Found {wimDriverInjectorDirs.Count} WIMDriverInjector folder(s).");
        
        // Collect all session directories and mount directories
        var allSessionDirs = new List<string>();
        var allMountDirs = new List<string>();
        
        foreach (var parentDir in wimDriverInjectorDirs)
        {
            try
            {
                var sessionDirs = Directory.GetDirectories(parentDir);
                allSessionDirs.AddRange(sessionDirs);
                
                foreach (var sessionDir in sessionDirs)
                {
                    try
                    {
                        var mountDirs = Directory.GetDirectories(sessionDir, "mount_*");
                        allMountDirs.AddRange(mountDirs);
                        
                        // Also check for retry mount directories
                        var retryMountDirs = Directory.GetDirectories(sessionDir, "mount_*_retry*");
                        allMountDirs.AddRange(retryMountDirs);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ============================================
        // STEP 1: Attempt to dismount all WIM directories
        // ============================================
        if (allMountDirs.Count > 0)
        {
            Log($"Step 1: Attempting to dismount {allMountDirs.Count} WIM mount(s)...");
            
            foreach (var mountDir in allMountDirs)
            {
                try
                {
                    Log($"  Dismounting: {mountDir}");
                    // Use synchronous DISM call first for immediate feedback
                    var (output, error, exitCode) = await RunProcessAsync("dism.exe", $"/Unmount-Wim /MountDir:\"{mountDir}\" /Discard");
                    
                    if (exitCode == 0)
                    {
                        Log($"    Successfully dismounted");
                    }
                    else
                    {
                        // Try starting in background as fallback
                        Log($"    Direct dismount failed (exit code {exitCode}), starting background dismount...");
                        try
                        {
                            var startInfo = new ProcessStartInfo
                            {
                                FileName = "cmd.exe",
                                Arguments = $"/c start \"\" /b dism.exe /Unmount-Wim /MountDir:\"{mountDir}\" /Discard",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            using var launcher = Process.Start(startInfo);
                            if (launcher != null)
                                await launcher.WaitForExitAsync();
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Log($"    Error dismounting: {ex.Message}");
                }
            }
            
            // Give DISM processes a moment to complete
            Log("  Waiting for dismount operations to settle...");
            await Task.Delay(2000);
        }
        else
        {
            Log("Step 1: No WIM mounts found to dismount.");
        }

        // ============================================
        // STEP 2: Run dism.exe /Cleanup-Wim
        // ============================================
        Log("Step 2: Running DISM /Cleanup-Wim to clean up orphaned mounts...");
        try
        {
            var (cleanupOutput, cleanupError, cleanupExitCode) = await RunProcessAsync("dism.exe", "/Cleanup-Wim");
            if (cleanupExitCode == 0)
            {
                Log("  DISM Cleanup-Wim completed successfully");
            }
            else
            {
                Log($"  DISM Cleanup-Wim returned exit code {cleanupExitCode}");
                if (!string.IsNullOrWhiteSpace(cleanupError))
                    Log($"  Error: {cleanupError.Trim()}");
            }
        }
        catch (Exception ex)
        {
            Log($"  Error running Cleanup-Wim: {ex.Message}");
        }
        
        // Give the system a moment after cleanup
        await Task.Delay(1000);

        // ============================================
        // STEP 3: Take ownership and permissions on all folders
        // ============================================
        Log($"Step 3: Taking ownership and resetting permissions on {allSessionDirs.Count} session folder(s)...");
        foreach (var sessionDir in allSessionDirs)
        {
            if (!Directory.Exists(sessionDir))
            {
                Log($"  Skipping (already deleted): {Path.GetFileName(sessionDir)}");
                continue;
            }
            
            try
            {
                Log($"  Processing: {Path.GetFileName(sessionDir)}");
                await TakeOwnershipAndResetPermissions(sessionDir);
            }
            catch (Exception ex)
            {
                Log($"    Error: {ex.Message}");
            }
        }
        
        // Small delay after permission changes
        await Task.Delay(500);

        // ============================================
        // STEP 4: Attempt to delete all session folders
        // ============================================
        Log($"Step 4: Attempting to delete {allSessionDirs.Count} session folder(s)...");
        var remainingDirs = new List<string>();
        
        foreach (var sessionDir in allSessionDirs)
        {
            if (!Directory.Exists(sessionDir))
            {
                Log($"  Already deleted: {Path.GetFileName(sessionDir)}");
                continue;
            }
            
            try
            {
                Log($"  Deleting: {Path.GetFileName(sessionDir)}");
                
                // First try standard delete
                try
                {
                    // Remove read-only attributes from all files first
                    foreach (var file in Directory.GetFiles(sessionDir, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                        }
                        catch { }
                    }
                    
                    Directory.Delete(sessionDir, true);
                    Log($"    Deleted successfully");
                    continue;
                }
                catch
                {
                    // Try force delete with PowerShell
                    Log($"    Standard delete failed, trying force delete...");
                    var forceDeleted = await ForceDelete(sessionDir);
                    if (forceDeleted)
                    {
                        Log($"    Force deleted successfully");
                        continue;
                    }
                }
                
                // Still exists - add to remaining
                if (Directory.Exists(sessionDir))
                {
                    Log($"    Could not delete, will schedule for cleanup");
                    remainingDirs.Add(sessionDir);
                }
            }
            catch (Exception ex)
            {
                Log($"    Error deleting: {ex.Message}");
                if (Directory.Exists(sessionDir))
                    remainingDirs.Add(sessionDir);
            }
        }

        // ============================================
        // STEP 5: Schedule cleanup tasks for remaining directories
        // ============================================
        var needsRestart = false;
        if (remainingDirs.Count > 0)
        {
            Log($"Step 5: Scheduling cleanup tasks for {remainingDirs.Count} remaining folder(s)...");
            foreach (var sessionDir in remainingDirs)
            {
                try
                {
                    Log($"  Scheduling: {Path.GetFileName(sessionDir)}");
                    await ScheduleCleanupTaskOnReboot(sessionDir);
                    needsRestart = true;
                }
                catch (Exception ex)
                {
                    Log($"    Error scheduling: {ex.Message}");
                }
            }
        }
        else
        {
            Log("Step 5: No remaining folders to schedule for cleanup.");
        }

        // Clean up empty parent directories
        foreach (var parentDir in wimDriverInjectorDirs)
        {
            try
            {
                if (Directory.Exists(parentDir) && !Directory.EnumerateFileSystemEntries(parentDir).Any())
                {
                    Directory.Delete(parentDir);
                    Log($"Removed empty parent directory: {parentDir}");
                }
            }
            catch { }
        }

        Log("Sweep complete.");

        if (needsRestart)
            return (true, $"Cleanup attempted. {remainingDirs.Count} folder(s) could not be deleted and have been scheduled for removal on restart. Restart now to complete cleanup?");
        
        var totalCleaned = allSessionDirs.Count - remainingDirs.Count;
        return (false, $"Sweep complete. Cleaned {totalCleaned} session folder(s) from {wimDriverInjectorDirs.Count} location(s).");
    }

    private static async Task<(string output, string error, int exitCode)> RunProcessAsync(string fileName, string arguments)
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
            return ("", "", -1);
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (output, error, process.ExitCode);
    }

    private static async Task TakeOwnershipAndResetPermissions(string directoryPath)
    {
        using (var takeown = Process.Start(new ProcessStartInfo
        {
            FileName = "takeown.exe",
            Arguments = $"/F \"{directoryPath}\" /R /D Y",
            UseShellExecute = false,
            CreateNoWindow = true
        }))
        {
            if (takeown != null) await takeown.WaitForExitAsync();
        }
        using (var icacls = Process.Start(new ProcessStartInfo
        {
            FileName = "icacls.exe",
            Arguments = $"\"{directoryPath}\" /grant Administrators:F /T /C /Q",
            UseShellExecute = false,
            CreateNoWindow = true
        }))
        {
            if (icacls != null) await icacls.WaitForExitAsync();
        }
    }

    private static async Task<bool> ForceDelete(string path)
    {
        try
        {
            var script = $@"Remove-Item -Path '{path.Replace("'", "''")}' -Force -Recurse -ErrorAction SilentlyContinue";
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process != null) await process.WaitForExitAsync();
            return !Directory.Exists(path);
        }
        catch { return false; }
    }

    private static async Task ScheduleCleanupTaskOnReboot(string directoryPath)
    {
        var taskName = "WIMDriverInjector_Cleanup_" + Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (taskName.Length > 200)
            taskName = "WIMDriverInjector_Cleanup_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var scriptDir = Path.Combine(programData, "WIMDriverInjector");
            Directory.CreateDirectory(scriptDir);
            var batchPath = Path.Combine(scriptDir, "Cleanup_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bat");
            var batchContent = string.Format(
@"@echo off
takeown /F ""{0}"" /R /D Y
icacls ""{0}"" /grant Administrators:F /T /C /Q
rd /s /q ""{0}""
schtasks /delete /tn ""{1}"" /f
del ""%~f0""
",
                directoryPath.Replace("\"", "\"\""),
                taskName.Replace("\"", "\"\""));
            await File.WriteAllTextAsync(batchPath, batchContent);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/create /tn \"{taskName}\" /tr \"{batchPath}\" /sc onstart /ru SYSTEM /rl HIGHEST /f",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process != null) await process.WaitForExitAsync();
        }
        catch { }
    }
}
