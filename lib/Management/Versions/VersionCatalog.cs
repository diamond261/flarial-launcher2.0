using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;
using Flarial.Launcher.Services.Core;
using Flarial.Launcher.Services.Networking;

namespace Flarial.Launcher.Services.Management.Versions;

public sealed class VersionCatalog
{
    VersionCatalog(HashSet<string> supported, SortedDictionary<string, VersionEntry> entries, List<CatalogEntry> installableEntries, string latestUwpVersion, string latestGdkVersion)
        => (_supported, _entries, _installableEntries, LatestUwpVersion, LatestGdkVersion) = (supported, entries, installableEntries, latestUwpVersion, latestGdkVersion);

    static readonly Comparer s_comparer = new();
    const string SupportedUri = "https://cdn.flarial.xyz/launcher/Supported.json";
    static readonly DataContractJsonSerializer s_supportedSerializer = new(typeof(Dictionary<string, bool>), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

    static string WithCacheBust(string uri) => $"{uri}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

    internal static void Log(string message)
    {
        try
        {
            var basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flarial", "Launcher", "Logs");
            Directory.CreateDirectory(basePath);
            var logPath = Path.Combine(basePath, "launcher.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] {message}{Environment.NewLine}");
        }
        catch { }
    }

    readonly HashSet<string> _supported;
    readonly SortedDictionary<string, VersionEntry> _entries;
    readonly List<CatalogEntry> _installableEntries;

    public VersionEntry this[string version] => _entries[version];
    public IEnumerable<string> InstallableVersions => _entries.Keys;
    public IEnumerable<CatalogEntry> InstallableEntries => _installableEntries;
    public string LatestUwpVersion { get; }
    public string LatestGdkVersion { get; }
    public bool IsSupported
    {
        get
        {
            var version = Minecraft.Version;
            var normalized = NormalizeVersion(version);
            return normalized.Length > 0 && _supported.Contains(normalized);
        }
    }

    public sealed class CatalogEntry
    {
        internal CatalogEntry(string version, string platform, VersionEntry entry, bool isSupported)
            => (Version, Platform, Entry, IsSupported) = (version, platform, entry, isSupported);

        public string Version { get; }
        public string Platform { get; }
        public VersionEntry Entry { get; }
        public bool IsSupported { get; }
    }

    public static string NormalizeVersion(string value)
        => TryNormalizeVersion(value, out var normalized, out _) ? normalized : string.Empty;

    internal static bool TryNormalizeVersion(string value, out string normalized, out Version parsedVersion)
    {
        normalized = string.Empty;
        parsedVersion = new Version(0, 0);

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var segments = value.Trim().Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is < 3 or > 4)
            return false;

        if (!int.TryParse(segments[0], out var major)
            || !int.TryParse(segments[1], out var minor)
            || !int.TryParse(segments[2], out var build))
        {
            return false;
        }

        normalized = $"{major}.{minor}.{build}";

        if (segments.Length == 4)
        {
            if (!int.TryParse(segments[3], out var revision))
                return false;

            parsedVersion = new Version(major, minor, build, revision);
            return true;
        }

        parsedVersion = new Version(major, minor, build);
        return true;
    }

    internal static bool ShouldReplaceDuplicate(Version candidateVersion, Version existingVersion)
        => candidateVersion.CompareTo(existingVersion) > 0;

    public static int CompareVersions(string left, string right)
    {
        var leftValid = TryNormalizeVersion(left, out var leftNormalized, out var leftVersion);
        var rightValid = TryNormalizeVersion(right, out var rightNormalized, out var rightVersion);

        if (leftValid && rightValid)
        {
            var versionComparison = rightVersion.CompareTo(leftVersion);
            return versionComparison != 0
                ? versionComparison
                : StringComparer.OrdinalIgnoreCase.Compare(leftNormalized, rightNormalized);
        }

        if (leftValid)
            return -1;

        if (rightValid)
            return 1;

        return StringComparer.OrdinalIgnoreCase.Compare(right ?? string.Empty, left ?? string.Empty);
    }

    static async Task<HashSet<string>> SupportedAsync()
    {
        HashSet<string> supported = [];

        try
        {
            using var stream = await HttpService.GetAsync<Stream>(WithCacheBust(SupportedUri));
            var payload = s_supportedSerializer.ReadObject(stream);
            var values = (Dictionary<string, bool>)payload;

            foreach (var item in values)
            {
                if (!item.Value)
                    continue;

                if (!TryNormalizeVersion(item.Key, out var value, out _))
                {
                    Log($"VersionCatalog supported version ignored | value={item.Key}");
                    continue;
                }

                supported.Add(value);
            }
        }
        catch (Exception exception)
        {
            Log($"VersionCatalog supported source failed | source=Supported.json | exception={exception.GetType().Name} | message={SanitizeLogValue(exception.Message)}");
        }

        return supported;
    }

    static async Task<Dictionary<string, VersionEntry>> LoadEntriesAsync(string source, Func<Task<Dictionary<string, VersionEntry>>> loader)
    {
        try
        {
            var entries = await loader() ?? [];
            Log($"VersionCatalog source loaded | source={source} | entries={entries.Count}");
            return entries;
        }
        catch (Exception exception)
        {
            Log($"VersionCatalog source failed | source={source} | exception={exception.GetType().Name} | message={SanitizeLogValue(exception.Message)}");
            return [];
        }
    }

    static async Task<VersionCatalog> GetAsync(Func<Task<HashSet<string>>> supportedProvider, Func<Task<Dictionary<string, VersionEntry>>> uwpProvider, Func<Task<Dictionary<string, VersionEntry>>> gdkProvider)
    {
        var supportedTask = supportedProvider();
        var uwpTask = LoadEntriesAsync("UWP", uwpProvider);
        var gdkTask = LoadEntriesAsync("GDK", gdkProvider);
        await Task.WhenAll(uwpTask, gdkTask, supportedTask);

        var supported = await supportedTask;
        var uwpEntries = await uwpTask;
        var gdkEntries = await gdkTask;

        List<CatalogEntry> installableEntries = [];
        SortedDictionary<string, VersionEntry> entries = new(s_comparer);
        Dictionary<string, string> entryPlatforms = new(StringComparer.OrdinalIgnoreCase);

        foreach (var item in uwpEntries)
            installableEntries.Add(new(item.Key, "UWP", item.Value, supported.Contains(item.Key)));

        foreach (var item in gdkEntries)
            installableEntries.Add(new(item.Key, "GDK", item.Value, supported.Contains(item.Key)));

        installableEntries.Sort((left, right) =>
        {
            var versionComparison = CompareVersions(left.Version, right.Version);
            return versionComparison != 0
                ? versionComparison
                : StringComparer.OrdinalIgnoreCase.Compare(left.Platform, right.Platform);
        });

        foreach (var item in installableEntries)
        {
            if (entries.ContainsKey(item.Version))
            {
                Log($"VersionCatalog duplicate normalized version ignored | version={item.Version} | keptPlatform={entryPlatforms[item.Version]} | ignoredPlatform={item.Platform}");
                continue;
            }

            entries.Add(item.Version, item.Entry);
            entryPlatforms[item.Version] = item.Platform;
        }

        var uwpLatest = GetLatestVersion(installableEntries, "UWP");
        var gdkLatest = GetLatestVersion(installableEntries, "GDK");
        Log($"VersionCatalog loaded | UWP latest={uwpLatest} count={installableEntries.Count(_ => _.Platform == "UWP")} | GDK latest={gdkLatest} count={installableEntries.Count(_ => _.Platform == "GDK")} | supported={supported.Count} | installable={installableEntries.Count}");

        return new(supported, entries, installableEntries, uwpLatest, gdkLatest);
    }

    public static async Task<VersionCatalog> GetAsync()
        => await GetAsync(SupportedAsync, UWPVersionEntry.GetAsync, GDKVersionEntry.GetAsync);

    public static async Task<string[]> VerifyAsync(string scenario)
    {
        var normalizedScenario = string.IsNullOrWhiteSpace(scenario) ? "all" : scenario.Trim().ToLowerInvariant();

        return normalizedScenario switch
        {
            "all" => await VerifyAllAsync(),
            "duplicate-preference" => await VerifyDuplicatePreferenceAsync(),
            "normal" => await VerifyNormalAsync(),
            "malformed" => await VerifyMalformedAsync(),
            "partial-failure" => await VerifyPartialFailureAsync(),
            _ => throw new InvalidOperationException($"UnknownScenario:{normalizedScenario}")
        };
    }

    static async Task<string[]> VerifyAllAsync()
    {
        List<string> lines = [];
        lines.AddRange(await VerifyNormalAsync());
        lines.AddRange(await VerifyDuplicatePreferenceAsync());
        lines.AddRange(await VerifyMalformedAsync());
        lines.AddRange(await VerifyPartialFailureAsync());
        return [.. lines];
    }

    static async Task<string[]> VerifyNormalAsync()
    {
        var catalog = await GetAsync(
            () => Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1.21.40.9" }),
            () => Task.FromResult(UWPVersionEntry.ParseEntries([
                ["1.21.50.4", "uwp-new", 0],
                ["1.21.40.2", "uwp-old", 0]
            ])),
            () => Task.FromResult(GDKVersionEntry.ParseEntries(new Dictionary<string, Dictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
            {
                ["release"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.21.60.8"] = ["https://example.test/gdk/1.21.60"],
                    ["1.21.55.4"] = ["https://example.test/gdk/1.21.55"]
                }
            })));

        var ordered = catalog.InstallableEntries.Select(_ => $"{_.Platform}:{_.Version}").ToArray();
        AssertSequence(ordered, "GDK:1.21.60", "GDK:1.21.55", "UWP:1.21.50", "UWP:1.21.40");
        Assert(catalog.LatestUwpVersion == "1.21.50", "NormalLatestUwpMismatch");
        Assert(catalog.LatestGdkVersion == "1.21.60", "NormalLatestGdkMismatch");
        return
        [
            "latestUwp=1.21.50 latestGdk=1.21.60 installable=4 order=GDK:1.21.60,GDK:1.21.55,UWP:1.21.50,UWP:1.21.40"
        ];
    }

    static async Task<string[]> VerifyMalformedAsync()
    {
        var uwpEntries = UWPVersionEntry.ParseEntries(
        [
            ["1.21.50.4", "uwp-keep", 0],
            ["1.21.50.9", "uwp-duplicate", 0],
            ["bad-version", "uwp-bad", 0],
            ["1.21.49.1", "", 0],
            ["1.21.48.3", "uwp-disabled", 1]
        ]);

        var gdkEntries = GDKVersionEntry.ParseEntries(new Dictionary<string, Dictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["release"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["1.21.40.8"] = ["https://example.test/gdk/1.21.40"],
                ["1.21.39.5"] = ["", "https://example.test/gdk/1.21.39"],
                ["broken"] = ["https://example.test/gdk/bad"],
                ["1.21.38.7"] = []
            }
        });

        var catalog = await GetAsync(
            () => Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            () => Task.FromResult(uwpEntries),
            () => Task.FromResult(gdkEntries));

        var ordered = catalog.InstallableEntries.Select(_ => $"{_.Platform}:{_.Version}").ToArray();
        AssertSequence(ordered, "UWP:1.21.50", "GDK:1.21.40", "GDK:1.21.39");
        Assert(catalog.LatestUwpVersion == "1.21.50", "MalformedLatestUwpMismatch");
        Assert(catalog.LatestGdkVersion == "1.21.40", "MalformedLatestGdkMismatch");
        return
        [
            "latestUwp=1.21.50 latestGdk=1.21.40 installable=3 malformedAndDuplicateRowsSkipped=true"
        ];
    }

    static async Task<string[]> VerifyDuplicatePreferenceAsync()
    {
        var catalog = await GetAsync(
            () => Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            () => Task.FromResult(UWPVersionEntry.ParseEntries(
            [
                ["1.21.50.1", "uwp-older", 0],
                ["1.21.50.9", "uwp-newer", 0],
                ["1.21.49.7", "uwp-previous", 0]
            ])),
            () => Task.FromResult(GDKVersionEntry.ParseEntries(new Dictionary<string, Dictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
            {
                ["release"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.21.60.2"] = ["https://example.test/gdk/1.21.60.2"],
                    ["1.21.60.8"] = ["https://example.test/gdk/1.21.60.8"],
                    ["1.21.58.4"] = ["https://example.test/gdk/1.21.58.4"]
                }
            })));

        var ordered = catalog.InstallableEntries.Select(_ => $"{_.Platform}:{_.Version}").ToArray();
        AssertSequence(ordered, "GDK:1.21.60", "GDK:1.21.58", "UWP:1.21.50", "UWP:1.21.49");
        Assert(catalog.LatestUwpVersion == "1.21.50", "DuplicatePreferenceLatestUwpMismatch");
        Assert(catalog.LatestGdkVersion == "1.21.60", "DuplicatePreferenceLatestGdkMismatch");
        return
        [
            "latestUwp=1.21.50 latestGdk=1.21.60 installable=4 duplicatePreference=newest-revision-kept"
        ];
    }

    static async Task<string[]> VerifyPartialFailureAsync()
    {
        var catalog = await GetAsync(
            () => Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            () => throw new HttpRequestException("Synthetic UWP failure"),
            () => Task.FromResult(GDKVersionEntry.ParseEntries(new Dictionary<string, Dictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
            {
                ["release"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.21.60.8"] = ["https://example.test/gdk/1.21.60"],
                    ["1.21.58.3"] = ["https://example.test/gdk/1.21.58"]
                }
            })));

        var ordered = catalog.InstallableEntries.Select(_ => $"{_.Platform}:{_.Version}").ToArray();
        AssertSequence(ordered, "GDK:1.21.60", "GDK:1.21.58");
        Assert(catalog.LatestUwpVersion == "n/a", "PartialFailureLatestUwpMismatch");
        Assert(catalog.LatestGdkVersion == "1.21.60", "PartialFailureLatestGdkMismatch");
        return
        [
            "latestUwp=n/a latestGdk=1.21.60 installable=2 failedSource=UWP survivingSource=GDK"
        ];
    }

    static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    static void AssertSequence(string[] actual, params string[] expected)
    {
        Assert(actual.Length == expected.Length, $"SequenceLengthMismatch:{actual.Length}:{expected.Length}");

        for (var index = 0; index < expected.Length; index++)
            Assert(string.Equals(actual[index], expected[index], StringComparison.OrdinalIgnoreCase), $"SequenceMismatch:{index}:{actual[index]}:{expected[index]}");
    }

    static string SanitizeLogValue(string value)
        => string.IsNullOrWhiteSpace(value)
            ? "n/a"
            : value.Replace(Environment.NewLine, " ").Trim();

    static string GetLatestVersion(IEnumerable<CatalogEntry> entries, string platform)
        => entries.FirstOrDefault(_ => string.Equals(_.Platform, platform, StringComparison.OrdinalIgnoreCase))?.Version ?? "n/a";

    sealed class Comparer : IComparer<string>
    {
        public int Compare(string x, string y) => CompareVersions(x, y);
    }
}
