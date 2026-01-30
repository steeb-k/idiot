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
        var needsRestart = false;
        var sessionsScheduled = new List<string>();
        var folderIndex = 0;

        foreach (var parentDir in wimDriverInjectorDirs)
        {
            folderIndex++;
            Log($"Processing folder {folderIndex}/{wimDriverInjectorDirs.Count}: {parentDir}");
            string[] sessionDirs;
            try
            {
                sessionDirs = Directory.GetDirectories(parentDir);
            }
            catch
            {
                continue;
            }

            foreach (var sessionDir in sessionDirs)
            {
                var mountDirs = new List<string>();
                try
                {
                    mountDirs.AddRange(Directory.GetDirectories(sessionDir, "mount_*"));
                }
                catch { }

                if (mountDirs.Count > 0)
                    Log($"Dismounting {mountDirs.Count} WIM mount(s) in {Path.GetFileName(sessionDir)}...");
                var unmountFailed = false;
                foreach (var mountDir in mountDirs)
                {
                    try
                    {
                        var (_, _, exitCode) = await RunProcessAsync("dism.exe", $"/Unmount-Wim /MountDir:\"{mountDir}\" /Discard");
                        if (exitCode != 0)
                            unmountFailed = true;
                    }
                    catch
                    {
                        unmountFailed = true;
                    }
                }

                await Task.Delay(500);

                Log($"Taking ownership and deleting: {Path.GetFileName(sessionDir)}...");
                try
                {
                    await TakeOwnershipAndResetPermissions(sessionDir);
                }
                catch { }

                try
                {
                    await Task.Delay(300);
                    if (Directory.Exists(sessionDir))
                    {
                        try
                        {
                            Directory.Delete(sessionDir, true);
                        }
                        catch
                        {
                            await ForceDelete(sessionDir);
                            if (Directory.Exists(sessionDir))
                                unmountFailed = true;
                        }
                    }
                }
                catch { }

                if (unmountFailed && Directory.Exists(sessionDir))
                {
                    Log($"Scheduling cleanup on restart for: {sessionDir}");
                    await ScheduleCleanupTaskOnReboot(sessionDir);
                    sessionsScheduled.Add(sessionDir);
                    needsRestart = true;
                }
            }

            try
            {
                if (Directory.Exists(parentDir) && !Directory.EnumerateFileSystemEntries(parentDir).Any())
                    Directory.Delete(parentDir);
            }
            catch { }
        }

        Log("Sweep complete.");

        if (needsRestart)
            return (true, $"Cleanup attempted. Some WIM mounts could not be dismounted. A scheduled task will remove them on the next restart. Restart now to complete cleanup?");
        return (false, $"Sweep complete. Cleaned WIMDriverInjector folders from {wimDriverInjectorDirs.Count} location(s).");
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
