using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Flarial.Launcher.Services.Networking;
using Microsoft.PowerShell;

namespace Flarial.Launcher.Services.Management.Versions;

sealed class UWPVersionEntry : VersionEntry
{
    const string MediaType = "application/soap+xml";
    const string DownloadUri = "http://tlu.dl.delivery.mp.microsoft.com";
    const string StoreUri = "https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx/secured";
    const string PackagesUri = "https://cdn.jsdelivr.net/gh/ddf8196/mc-w10-versiondb-auto-update@refs/heads/master/versions.json.min";


    readonly string _content;
    static readonly string s_content;
    static readonly DataContractJsonSerializer s_serializer = new(typeof(object[][]), s_settings);

    internal override InstallRequest.PackageInstallKind InstallKind => InstallRequest.PackageInstallKind.Uwp;

    static string WithCacheBust(string uri) => $"{uri}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

    UWPVersionEntry(string identifier) : base() => _content = string.Format(s_content, identifier, '1');

    static UWPVersionEntry()
    {
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream("GetExtendedUpdateInfo2.xml");
        using StreamReader reader = new(stream);

        s_content = reader.ReadToEnd();
    }

    internal static async Task<Dictionary<string, VersionEntry>> GetAsync() => await Task.Run(async () =>
    {
        using var stream = await HttpService.GetAsync<Stream>(WithCacheBust(PackagesUri));
        var @object = s_serializer.ReadObject(stream);
        return ParseEntries((object[][])@object);
    });

    internal static Dictionary<string, VersionEntry> ParseEntries(object[][] collection)
    {
        if (collection is null)
            throw new InvalidDataException("UWP catalog payload was empty.");

        Dictionary<string, VersionEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Version> parsedVersions = new(StringComparer.OrdinalIgnoreCase);

        foreach (var item in collection)
        {
            if (item is null)
            {
                VersionCatalog.Log("VersionCatalog UWP row ignored | reason=NullRow");
                continue;
            }

            var isActive = true;

            if (item.Length < 2)
            {
                VersionCatalog.Log("VersionCatalog UWP row ignored | reason=MissingRequiredColumns");
                continue;
            }

            if (item.Length > 2 && !IsActive(item[2], out isActive))
            {
                VersionCatalog.Log($"VersionCatalog UWP row ignored | reason=InvalidAvailabilityFlag | value={item[2]}");
                continue;
            }
            else if (item.Length > 2 && !isActive)
            {
                continue;
            }

            var identifier = item[1]?.ToString()?.Trim() ?? string.Empty;
            if (identifier.Length == 0)
            {
                VersionCatalog.Log("VersionCatalog UWP row ignored | reason=MissingIdentifier");
                continue;
            }

            if (!VersionCatalog.TryNormalizeVersion(item[0]?.ToString() ?? string.Empty, out var version, out var parsedVersion))
            {
                VersionCatalog.Log($"VersionCatalog UWP row ignored | reason=InvalidVersion | value={item[0]}");
                continue;
            }

            if (entries.ContainsKey(version))
            {
                if (VersionCatalog.ShouldReplaceDuplicate(parsedVersion, parsedVersions[version]))
                {
                    entries[version] = new UWPVersionEntry(identifier);
                    parsedVersions[version] = parsedVersion;
                    VersionCatalog.Log($"VersionCatalog UWP duplicate replaced | version={version} | identifier={identifier}");
                    continue;
                }

                VersionCatalog.Log($"VersionCatalog UWP duplicate ignored | version={version} | identifier={identifier}");
                continue;
            }

            entries.Add(version, new UWPVersionEntry(identifier));
            parsedVersions.Add(version, parsedVersion);
        }

        return entries;
    }

    static bool IsActive(object value, out bool isActive)
    {
        isActive = false;

        if (value is null)
            return false;

        if (!int.TryParse(value.ToString(), out var flag))
            return false;

        isActive = flag == 0;
        return true;
    }

    internal override async Task<string[]> UrisAsync() => await Task.Run(async () =>
    {
        using StringContent content = new(_content, Encoding.UTF8, MediaType);
        using var message = await HttpService.PostAsync(StoreUri, content);

        message.EnsureSuccessStatusCode();
        using var stream = await message.Content.ReadAsStreamAsync();

        return XElement.Load(stream)
            .Descendants()
            .Select(_ => _.Value?.Trim())
            .Where(_ => !string.IsNullOrWhiteSpace(_))
            .Cast<string>()
            .Where(_ => _.StartsWith(DownloadUri, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    });

    internal override async Task<string> UriAsync()
        => (await UrisAsync()).First();
}
