using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;
using Flarial.Launcher.Services.Networking;

namespace Flarial.Launcher.Services.Management.Versions;


sealed class GDKVersionEntry : VersionEntry
{
    const string PackagesUri = "https://cdn.jsdelivr.net/gh/MinecraftBedrockArchiver/GdkLinks@latest/urls.json";

    static readonly DataContractJsonSerializer s_serializer = new(typeof(Dictionary<string, Dictionary<string, string[]>>), s_settings);

    readonly string[] _urls;

    internal override InstallRequest.PackageInstallKind InstallKind => InstallRequest.PackageInstallKind.Gdk;

    static string WithCacheBust(string uri) => $"{uri}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

    GDKVersionEntry(string[] urls) : base() => _urls = urls;

    internal static async Task<Dictionary<string, VersionEntry>> GetAsync() => await Task.Run(async () =>
    {
        using var stream = await HttpService.GetAsync<Stream>(WithCacheBust(PackagesUri));
        var @object = s_serializer.ReadObject(stream);
        return ParseEntries((Dictionary<string, Dictionary<string, string[]>>)@object);
    });

    internal static Dictionary<string, VersionEntry> ParseEntries(Dictionary<string, Dictionary<string, string[]>> collection)
    {
        if (collection is null)
            throw new InvalidDataException("GDK catalog payload was empty.");

        Dictionary<string, VersionEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Version> parsedVersions = new(StringComparer.OrdinalIgnoreCase);

        if (!collection.TryGetValue("release", out var release) || release is null)
            throw new InvalidDataException("GDK release catalog is missing the 'release' section.");

        foreach (var item in release)
        {
            if (!VersionCatalog.TryNormalizeVersion(item.Key, out var version, out var parsedVersion))
            {
                VersionCatalog.Log($"VersionCatalog GDK row ignored | reason=InvalidVersion | value={item.Key}");
                continue;
            }

            var urls = (item.Value ?? [])
                .Select(_ => _?.Trim())
                .Where(_ => !string.IsNullOrWhiteSpace(_))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (urls.Length == 0)
            {
                VersionCatalog.Log($"VersionCatalog GDK row ignored | reason=MissingUrls | version={version}");
                continue;
            }

            if (entries.ContainsKey(version))
            {
                if (VersionCatalog.ShouldReplaceDuplicate(parsedVersion, parsedVersions[version]))
                {
                    entries[version] = new GDKVersionEntry(urls);
                    parsedVersions[version] = parsedVersion;
                    VersionCatalog.Log($"VersionCatalog GDK duplicate replaced | version={version} | urlCount={urls.Length}");
                    continue;
                }

                VersionCatalog.Log($"VersionCatalog GDK duplicate ignored | version={version} | urlCount={urls.Length}");
                continue;
            }

            entries.Add(version, new GDKVersionEntry(urls));
            parsedVersions.Add(version, parsedVersion);
        }

        return entries;
    }

    internal override async Task<string> UriAsync() => _urls[0];

    internal override Task<string[]> UrisAsync()
    {
        var urls = _urls
            .Where(_ => !string.IsNullOrWhiteSpace(_))
            .ToArray();
        return Task.FromResult(urls);
    }
}
