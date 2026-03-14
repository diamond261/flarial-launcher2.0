using System;
using Flarial.Launcher.Services.Management.Versions;

namespace Flarial.Launcher;

internal static class VersionCatalogVerificationRunner
{
    const string Prefix = "VERSION_CATALOG_VERIFY";

    public static int Run(string scenario)
    {
        var normalized = string.IsNullOrWhiteSpace(scenario) ? "all" : scenario.Trim().ToLowerInvariant();

        try
        {
            var lines = VersionCatalog.VerifyAsync(normalized).GetAwaiter().GetResult();

            foreach (var line in lines)
                WriteLine($"{Prefix} scenario={normalized} result=pass {line}");

            if (normalized == "all")
                WriteLine($"{Prefix} scenario=all result=pass");

            return 0;
        }
        catch (Exception exception)
        {
            var message = $"{Prefix} scenario={normalized} result={(exception.Message.StartsWith("UnknownScenario:", StringComparison.OrdinalIgnoreCase) ? "invalid" : "fail")} message={Sanitize(exception.Message)}";
            Logger.Error(message, exception);
            TryWriteToConsole(message);
            return message.Contains("result=invalid", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        }
    }

    static void WriteLine(string message)
    {
        Logger.Info(message);
        TryWriteToConsole(message);
    }

    static void TryWriteToConsole(string message)
    {
        try { Console.WriteLine(message); }
        catch { }
    }

    static string Sanitize(string value) => value.Replace(' ', '_');
}
