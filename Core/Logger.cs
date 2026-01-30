using System;
using System.IO;
using System.Text;

namespace WIMISODriverInjector.Core
{
    public class Logger
    {
        private readonly string _logFilePath;
        private readonly StringBuilder _logBuffer;
        private readonly object _lockObject = new object();

        public string LogFilePath => _logFilePath;

        public Logger(string logFilePath)
        {
            _logFilePath = logFilePath;
            _logBuffer = new StringBuilder();
            
            // Initialize log file
            var directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Create the log file immediately to ensure it exists
            try
            {
                File.WriteAllText(_logFilePath, "", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not create log file: {ex.Message}");
            }

            LogInfo($"=== Driver Injection Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            Flush(); // Flush the initial log entry
        }

        public void LogInfo(string message)
        {
            Log("INFO", message);
        }

        public void LogWarning(string message)
        {
            Log("WARNING", message);
        }

        public void LogError(string message)
        {
            Log("ERROR", message);
        }

        public void LogSuccess(string message)
        {
            Log("SUCCESS", message);
        }

        public void LogDriverFailure(string driverPath, string reason)
        {
            Log("DRIVER_FAILED", $"Driver: {driverPath} - Reason: {reason}");
        }

        private void Log(string level, string message)
        {
            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
            
            lock (_lockObject)
            {
                _logBuffer.AppendLine(logEntry);
                Console.WriteLine(logEntry);
                
                // Flush to file immediately for all messages (more verbose logging)
                try
                {
                    File.AppendAllText(_logFilePath, logEntry + Environment.NewLine, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to write to log file: {ex.Message}");
                }
            }
        }

        public void Flush()
        {
            lock (_lockObject)
            {
                try
                {
                    File.AppendAllText(_logFilePath, _logBuffer.ToString(), Encoding.UTF8);
                    _logBuffer.Clear();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to write to log file: {ex.Message}");
                }
            }
        }

        public void Save()
        {
            Flush();
        }
    }
}
