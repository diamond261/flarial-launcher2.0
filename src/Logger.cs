using System;
using System.Collections.Generic;
using System.IO;
using Flarial.Launcher.Managers;

namespace Flarial.Launcher;

static class Logger
{
    static readonly object s_lock = new();

    static string LogPath => Path.Combine(VersionManagement.launcherPath, "Logs", "launcher.log");

    static string FormatFields((string Key, object Value)[] fields)
    {
        List<string> lines = [];

        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
                continue;

            var text = value.ToString();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            lines.Add($"  {key}={text}");
        }

        return lines.Count == 0
            ? string.Empty
            : $"{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
    }

    static void Write(string level, string message)
    {
        try
        {
            lock (s_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    internal static void Info(string message) => Write("INFO", message);

    internal static void Error(string message, Exception exception)
        => Write("ERROR", $"{message}{Environment.NewLine}{exception}");

    internal static void Error(string message, Exception exception, params (string Key, object Value)[] fields)
        => Write("ERROR", $"{message}{FormatFields(fields)}{Environment.NewLine}{exception}");
}
