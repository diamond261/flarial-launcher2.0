using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management.Automation;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml;
using Flarial.Launcher.Services.Core;
using Flarial.Launcher.Services.Networking;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Management.Deployment;

namespace Flarial.Launcher.Services.Management.Versions;

public sealed class InstallRequest
{
    public enum PackageInstallKind
    {
        Uwp,
        Gdk
    }

    const int MaxAttemptsPerUri = 3;
    const string MinecraftPackageFamily = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";
    const string MinecraftPreviewPackageFamily = "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe";
    const string GdkConfigFileName = "MicrosoftGame.Config";
    const string BundleManifestFileName = "AppxMetadata/AppxBundleManifest.xml";
    static readonly string s_launcherPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flarial", "Launcher");

    sealed class PackageIdentityInfo
    {
        internal PackageIdentityInfo(string name, string publisher, Version version, string architecture, bool isFramework, string manifestName)
            => (Name, Publisher, Version, Architecture, IsFramework, ManifestName) = (name, publisher, version, architecture, isFramework, manifestName);

        internal string Name { get; }
        internal string Publisher { get; }
        internal Version Version { get; }
        internal string Architecture { get; }
        internal bool IsFramework { get; }
        internal string ManifestName { get; }
    }

    sealed class ManifestDependencyInfo
    {
        internal ManifestDependencyInfo(string name, string publisher, Version minVersion, bool optional)
            => (Name, Publisher, MinVersion, Optional) = (name, publisher, minVersion, optional);

        internal string Name { get; }
        internal string Publisher { get; }
        internal Version MinVersion { get; }
        internal bool Optional { get; }
    }

    sealed class FallbackDependencyCandidate
    {
        internal FallbackDependencyCandidate(string url, string architecture)
            => (Url, Architecture) = (url, architecture);

        internal string Url { get; }
        internal string Architecture { get; }
    }

    static readonly PackageManager s_manager = new();
    static readonly string s_stagingPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flarial", "Launcher", "InstallStaging");
    static readonly string s_versionsPath = Path.Combine(s_launcherPath, "Versions");
    static readonly ConcurrentDictionary<string, bool> s_paths = [];
    static readonly object s_logLock = new();
    static readonly SemaphoreSlim s_gdkInstallLock = new(1, 1);
    static readonly IReadOnlyDictionary<string, FallbackDependencyCandidate[]> s_fallbackDependencies = new Dictionary<string, FallbackDependencyCandidate[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Services.Store.Engagement"] =
        [
            new("https://raw.githubusercontent.com/mcrax/mcrev/master/Microsoft.Services.Store.Engagement_10.0.19011.0_x64__8wekyb3d8bbwe.Appx", "x64"),
            new("https://raw.githubusercontent.com/mcrax/mcrev/master/Microsoft.Services.Store.Engagement_10.0.19011.0_x86__8wekyb3d8bbwe.Appx", "x86")
        ]
    };
    static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flarial", "Launcher", "Logs", "launcher.log");

    static InstallRequest()
    {
        Directory.CreateDirectory(s_stagingPath);
        Directory.CreateDirectory(s_versionsPath);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var path in s_paths)
            {
                try
                {
                    if (File.Exists(path.Key))
                        File.Delete(path.Key);
                    else if (Directory.Exists(path.Key))
                        Directory.Delete(path.Key, true);
                }
                catch { }
            }
        };
    }

    static void LogInstall(string message, Exception? exception = null, params (string Key, object? Value)[] fields)
    {
        try
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

            lock (s_logLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{(exception is null ? "INFO" : "ERROR")}] {message}" +
                    (lines.Count == 0 ? string.Empty : Environment.NewLine + string.Join(Environment.NewLine, lines)) +
                    (exception is null ? string.Empty : Environment.NewLine + exception) +
                    Environment.NewLine);
            }
        }
        catch { }
    }

    static string NormalizeVersionLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        StringBuilder builder = new(value.Length);
        foreach (var character in value.Trim())
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_');

        return builder.ToString();
    }

    static bool IsPreviewVersion(string version)
        => version.IndexOf("preview", StringComparison.OrdinalIgnoreCase) >= 0 || version.IndexOf("beta", StringComparison.OrdinalIgnoreCase) >= 0;

    static string GetPackageFamily(string version)
        => IsPreviewVersion(version) ? MinecraftPreviewPackageFamily : MinecraftPackageFamily;

    static string GetMainPackageName(string version)
        => IsPreviewVersion(version) ? "Microsoft.MinecraftWindowsBeta" : "Microsoft.MinecraftUWP";

    static string GetInstallDirectory(string version, string platform)
        => Path.Combine(s_versionsPath, $"Minecraft-{NormalizeVersionLabel(version)}-{platform.ToUpperInvariant()}");

    static bool IsLockedFileFailure(Exception exception)
    {
        static bool MatchesMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            var candidate = message!;
            return candidate.IndexOf("used by another process", StringComparison.OrdinalIgnoreCase) >= 0
                || candidate.IndexOf("cannot access the file", StringComparison.OrdinalIgnoreCase) >= 0
                || candidate.IndexOf("sharing violation", StringComparison.OrdinalIgnoreCase) >= 0
                || candidate.IndexOf("being used by another process", StringComparison.OrdinalIgnoreCase) >= 0
                || candidate.IndexOf("access to the path", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        return exception switch
        {
            IOException ioException when MatchesMessage(ioException.Message) => true,
            UnauthorizedAccessException unauthorizedAccessException when MatchesMessage(unauthorizedAccessException.Message) => true,
            _ when exception.InnerException is not null => IsLockedFileFailure(exception.InnerException),
            _ => false
        };
    }

    static void TryDeleteWithBackoff(string path)
    {
        if (!File.Exists(path))
            return;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
            catch
            {
                return;
            }
        }
    }

    static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
            catch
            {
                return;
            }
        }
    }

    static Exception PreservePrimaryFailure(Exception primaryException, string path)
    {
        if (IsLockedFileFailure(primaryException))
            return new IOException($"The package staging file is locked by another process: {path}", primaryException);

        try
        {
            TryDeleteWithBackoff(path);
        }
        catch { }

        return primaryException;
    }

    static bool IsValidPackageFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0)
            return false;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[4];
        if (stream.Read(header, 0, header.Length) != header.Length)
            return false;

        return header[0] == (byte)'P' && header[1] == (byte)'K';
    }

    static string GetDownloadExtension(PackageInstallKind kind)
        => kind == PackageInstallKind.Uwp ? ".appx" : ".msixvc";

    static Version ParseVersion(string value)
        => Version.TryParse(value, out var version) ? version : new Version(0, 0, 0, 0);

    static string NormalizeArchitecture(string? value)
    {
        var architecture = value?.Trim();
        if (string.IsNullOrWhiteSpace(architecture))
            return "neutral";

        if (architecture is not null &&
            (architecture.Equals("x86", StringComparison.OrdinalIgnoreCase)
            || architecture.Equals("x64", StringComparison.OrdinalIgnoreCase)
            || architecture.Equals("arm", StringComparison.OrdinalIgnoreCase)
            || architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase)
            || architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase)))
            return architecture.ToLowerInvariant();

        return (architecture ?? "neutral").ToLowerInvariant();
    }

    static PackageIdentityInfo ReadPackageIdentity(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);

        var manifestEntry = archive.GetEntry("AppxManifest.xml");
        if (manifestEntry is not null)
            return ReadAppxManifestIdentity(manifestEntry, "AppxManifest.xml");

        var bundleEntry = archive.GetEntry(BundleManifestFileName);
        if (bundleEntry is not null)
            return ReadBundleManifestIdentity(bundleEntry);

        throw new InvalidDataException($"Package '{packagePath}' does not contain AppxManifest.xml or AppxBundleManifest.xml.");
    }

    static PackageIdentityInfo ReadAppxManifestIdentity(ZipArchiveEntry manifestEntry, string manifestName)
    {
        using var stream = manifestEntry.Open();
        var document = XDocument.Load(stream);
        XNamespace ns = document.Root?.Name.Namespace ?? "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

        var identity = document.Root?.Element(ns + "Identity") ?? throw new InvalidDataException($"Manifest '{manifestName}' is missing Identity.");
        var properties = document.Root?.Element(ns + "Properties");
        var frameworkValue = properties?.Element(ns + "Framework")?.Value;

        return new PackageIdentityInfo(
            identity.Attribute("Name")?.Value ?? throw new InvalidDataException($"Manifest '{manifestName}' is missing package name."),
            identity.Attribute("Publisher")?.Value ?? string.Empty,
            ParseVersion(identity.Attribute("Version")?.Value ?? string.Empty),
            NormalizeArchitecture(identity.Attribute("ProcessorArchitecture")?.Value),
            bool.TryParse(frameworkValue, out var isFramework) && isFramework,
            manifestName);
    }

    static PackageIdentityInfo ReadBundleManifestIdentity(ZipArchiveEntry bundleEntry)
    {
        using var stream = bundleEntry.Open();
        var document = XDocument.Load(stream);
        XNamespace ns = document.Root?.Name.Namespace ?? "http://schemas.microsoft.com/appx/2013/bundle";

        var identity = document.Root?.Element(ns + "Identity") ?? throw new InvalidDataException("Bundle manifest is missing Identity.");

        return new PackageIdentityInfo(
            identity.Attribute("Name")?.Value ?? throw new InvalidDataException("Bundle manifest is missing package name."),
            identity.Attribute("Publisher")?.Value ?? string.Empty,
            ParseVersion(identity.Attribute("Version")?.Value ?? string.Empty),
            NormalizeArchitecture(identity.Attribute("Architecture")?.Value),
            false,
            BundleManifestFileName);
    }

    static List<ManifestDependencyInfo> ReadManifestDependencies(string manifestPath)
    {
        XDocument document = XDocument.Load(manifestPath);
        XNamespace ns = document.Root?.Name.Namespace ?? "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

        List<ManifestDependencyInfo> dependencies = [];
        foreach (var dependency in document.Descendants(ns + "PackageDependency"))
        {
            var name = dependency.Attribute("Name")?.Value?.Trim();
            var publisher = dependency.Attribute("Publisher")?.Value?.Trim();
            var minVersion = dependency.Attribute("MinVersion")?.Value?.Trim();
            var optional = dependency.Attributes().FirstOrDefault(_ => _.Name.LocalName == "Optional")?.Value;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(publisher) || string.IsNullOrWhiteSpace(minVersion))
                continue;

            var resolvedName = name!;
            var resolvedPublisher = publisher!;

            dependencies.Add(new ManifestDependencyInfo(
                resolvedName,
                resolvedPublisher,
                ParseVersion(minVersion!),
                bool.TryParse(optional, out var isOptional) && isOptional));
        }

        return dependencies;
    }

    static bool InstalledPackageSatisfiesDependency(ManifestDependencyInfo dependency)
    {
        foreach (var package in s_manager.FindPackages())
        {
            var identity = package.Id;
            if (!identity.Name.Equals(dependency.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!identity.Publisher.Equals(dependency.Publisher, StringComparison.OrdinalIgnoreCase))
                continue;

            var version = new Version(identity.Version.Major, identity.Version.Minor, identity.Version.Build, identity.Version.Revision);
            if (version >= dependency.MinVersion)
                return true;
        }

        return false;
    }

    static bool PackageCanSatisfyDependency(PackageIdentityInfo identity, ManifestDependencyInfo dependency)
    {
        if (!identity.IsFramework)
            return false;

        if (!identity.Name.Equals(dependency.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!identity.Publisher.Equals(dependency.Publisher, StringComparison.OrdinalIgnoreCase))
            return false;

        return identity.Version >= dependency.MinVersion;
    }

    static bool ArchitectureMatchesMachine(string architecture)
    {
        var normalized = NormalizeArchitecture(architecture);
        if (normalized == "neutral")
            return true;

        if (normalized == "x64")
            return Environment.Is64BitOperatingSystem;

        if (normalized == "x86")
            return true;

        return false;
    }

    static async Task<string?> TryDownloadFallbackDependencyAsync(ManifestDependencyInfo dependency, string stagingDirectory, Action<int> action)
    {
        if (!s_fallbackDependencies.TryGetValue(dependency.Name, out var candidates))
            return null;

        foreach (var candidate in candidates.Where(_ => ArchitectureMatchesMachine(_.Architecture)))
        {
            var path = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + ".appx");
            s_paths.TryAdd(path, new());

            try
            {
                LogInstall("Attempting fallback dependency download", null,
                    ("DependencyName", dependency.Name),
                    ("DependencyMinVersion", dependency.MinVersion),
                    ("FallbackUrl", candidate.Url));

                await HttpService.DownloadAsync(candidate.Url, path, progress => action(70 + progress * 10 / 100));

                if (!IsValidPackageFile(path))
                    throw new InvalidDataException($"Downloaded fallback dependency failed validation from {candidate.Url}");

                var identity = ReadPackageIdentity(path);
                if (!PackageCanSatisfyDependency(identity, dependency))
                {
                    LogInstall("Fallback dependency rejected after download", null,
                        ("DependencyName", dependency.Name),
                        ("DownloadedName", identity.Name),
                        ("DownloadedVersion", identity.Version),
                        ("DownloadedPublisher", identity.Publisher),
                        ("DownloadedArchitecture", identity.Architecture));

                    TryDeleteWithBackoff(path);
                    s_paths.TryRemove(path, out _);
                    continue;
                }

                return path;
            }
            catch (Exception exception)
            {
                LogInstall("Fallback dependency download failed", exception,
                    ("DependencyName", dependency.Name),
                    ("FallbackUrl", candidate.Url));
                TryDeleteWithBackoff(path);
                s_paths.TryRemove(path, out _);
            }
        }

        return null;
    }

    static async Task InstallDependencyPackageAsync(string packagePath, Action<int> action)
    {
        await RunDeploymentAsync(s_manager.AddPackageAsync(new Uri(packagePath), null, DeploymentOptions.None), "add-dependency", action, 86, 89);
    }

    static async Task InstallUwpPackageAsync(string packagePath, string packageFamily, IReadOnlyDictionary<string, PackageIdentityInfo> availablePackages, string stagingDirectory, IList<string> transientDependencyPaths, Action<int> action)
    {
        var extractedDirectory = Path.Combine(stagingDirectory, "uwp-manifest");
        await ExtractAppxAsync(packagePath, extractedDirectory, action);

        var manifestPath = Path.Combine(extractedDirectory, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The downloaded UWP package is missing AppxManifest.xml.", manifestPath);

        var dependencyUris = await ResolveAndInstallDependenciesAsync(manifestPath, availablePackages, stagingDirectory, transientDependencyPaths, action);

        await UnregisterFamilyAsync(packageFamily, preserveApplicationData: true);
        action(90);
        await RunDeploymentAsync(s_manager.AddPackageAsync(new Uri(packagePath), dependencyUris, DeploymentOptions.None), "install", action, 90, 99);
        action(100);
    }

    static async Task<IReadOnlyList<Uri>> ResolveAndInstallDependenciesAsync(string manifestPath, IReadOnlyDictionary<string, PackageIdentityInfo> availablePackages, string stagingDirectory, IList<string> transientDependencyPaths, Action<int> action)
    {
        List<Uri> dependencyUris = [];
        var dependencies = ReadManifestDependencies(manifestPath);

        foreach (var dependency in dependencies.Where(_ => !_.Optional))
        {
            if (InstalledPackageSatisfiesDependency(dependency))
                continue;

            var packagePath = availablePackages
                .Where(_ => PackageCanSatisfyDependency(_.Value, dependency))
                .OrderByDescending(_ => _.Value.Version)
                .Select(_ => _.Key)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(packagePath))
            {
                packagePath = await TryDownloadFallbackDependencyAsync(dependency, stagingDirectory, action);
                if (!string.IsNullOrWhiteSpace(packagePath))
                    transientDependencyPaths.Add(packagePath!);
            }

            if (string.IsNullOrWhiteSpace(packagePath))
                throw new InvalidOperationException($"Missing required framework package: {dependency.Name} >= {dependency.MinVersion} ({dependency.Publisher}). The launcher could not obtain this dependency package.");

            LogInstall("Installing missing dependency package", null,
                ("DependencyName", dependency.Name),
                ("DependencyMinVersion", dependency.MinVersion),
                ("DependencyPackagePath", packagePath));

            await InstallDependencyPackageAsync(packagePath!, action);
            dependencyUris.Add(new Uri(packagePath!));
        }

        return dependencyUris;
    }

    static async Task<Dictionary<string, PackageIdentityInfo>> DownloadPackagesAsync(string[] uris, string stagingDirectory, PackageInstallKind kind, Action<int> action)
    {
        Dictionary<string, PackageIdentityInfo> packages = new(StringComparer.OrdinalIgnoreCase);
        Exception? lastException = null;
        var candidates = uris.Where(uri => !string.IsNullOrWhiteSpace(uri)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (candidates.Length == 0)
            throw new InvalidOperationException("No package URLs available.");

        for (var index = 0; index < candidates.Length; index++)
        {
            var uri = candidates[index];
            var path = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + GetDownloadExtension(kind));
            s_paths.TryAdd(path, new());

            try
            {
                await HttpService.DownloadAsync(uri, path, progress =>
                {
                    var scaledProgress = ((index * 100) + progress) / Math.Max(1, candidates.Length);
                    action(scaledProgress * 70 / 100);
                });

                if (!IsValidPackageFile(path))
                    throw new InvalidDataException($"Downloaded package failed validation from {uri}");

                packages[path] = ReadPackageIdentity(path);
            }
            catch (Exception exception)
            {
                lastException = PreservePrimaryFailure(exception, path);
                s_paths.TryRemove(path, out _);
                TryDeleteWithBackoff(path);
                break;
            }
        }

        if (lastException is not null)
            throw lastException;

        return packages;
    }

    static async Task<string> DownloadPackageAsync(string[] uris, string stagingDirectory, PackageInstallKind kind, Action<int> action)
    {
        Exception? lastException = null;
        var candidates = uris.Where(uri => !string.IsNullOrWhiteSpace(uri)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (candidates.Length == 0)
            throw new InvalidOperationException("No package URLs available.");

        foreach (var uri in candidates)
        {
            for (var attempt = 0; attempt < MaxAttemptsPerUri; attempt++)
            {
                var path = Path.Combine(stagingDirectory, Path.GetRandomFileName() + GetDownloadExtension(kind));
                s_paths.TryAdd(path, new());

                try
                {
                    await HttpService.DownloadAsync(uri, path, progress => action(progress * 70 / 100));

                    if (!IsValidPackageFile(path))
                        throw new InvalidDataException($"Downloaded package failed validation from {uri}");

                    return path;
                }
                catch (Exception exception) when (exception is IOException || exception is InvalidDataException)
                {
                    lastException = PreservePrimaryFailure(exception, path);
                    if (attempt >= MaxAttemptsPerUri - 1 || !IsLockedFileFailure(exception))
                        break;
                }
                catch (Exception exception)
                {
                    lastException = PreservePrimaryFailure(exception, path);
                    break;
                }
                finally
                {
                    if (lastException is not null)
                        s_paths.TryRemove(path, out _);
                }

                await Task.Delay(1000 * (attempt + 1));
            }

            await Task.Delay(250);
        }

        throw lastException ?? new InvalidOperationException("Download failed.");
    }

    static async Task ExtractAppxAsync(string packagePath, string installDirectory, Action<int> action)
    {
        action(75);
        await Task.Run(() =>
        {
            TryDeleteDirectory(installDirectory);
            Directory.CreateDirectory(installDirectory);
            ZipFile.ExtractToDirectory(packagePath, installDirectory);

            var signature = Path.Combine(installDirectory, "AppxSignature.p7x");
            if (File.Exists(signature))
                File.Delete(signature);
        });
        action(85);
    }

    static async Task<T> RunDeploymentAsync<T>(IAsyncOperationWithProgress<T, DeploymentProgress> operation, string operationName, Action<int> action, int minProgress, int maxProgress)
    {
        TaskCompletionSource<T> source = new(TaskCreationOptions.RunContinuationsAsynchronously);

        operation.Progress += (_, args) =>
        {
            var value = minProgress + ((int)args.percentage * Math.Max(1, maxProgress - minProgress) / 100);
            action(Math.Min(maxProgress, value));
        };

        operation.Completed += (sender, _) =>
        {
            if (sender.Status is AsyncStatus.Error)
            {
                source.TrySetException(new DeploymentOperationException(operationName, sender.ErrorCode));
                return;
            }

            if (sender.Status is AsyncStatus.Canceled)
            {
                source.TrySetException(new OperationCanceledException($"Windows package {operationName} was canceled."));
                return;
            }

            try
            {
                source.TrySetResult(sender.GetResults());
            }
            catch (Exception exception)
            {
                source.TrySetException(exception);
            }
        };

        var result = await source.Task;
        action(maxProgress);
        return result;
    }

    static string ResolveFinalPath(string directory)
        => Path.GetFullPath(directory);

    static void RecursiveCopyDirectory(string from, string to, HashSet<string> skip)
    {
        Directory.CreateDirectory(to);

        foreach (var source in Directory.EnumerateFiles(from))
        {
            if (skip.Contains(source))
                continue;

            File.Copy(source, Path.Combine(to, Path.GetFileName(source)), true);
        }

        foreach (var source in Directory.EnumerateDirectories(from))
            RecursiveCopyDirectory(source, Path.Combine(to, Path.GetFileName(source)), skip);
    }

    static void FixGdkManifest(string path)
    {
        XDocument document = XDocument.Load(path);
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

        foreach (var app in document.Descendants(ns + "Application"))
        {
            var executable = app.Attribute("Executable");
            if (executable is not null && executable.Value == "GameLaunchHelper.exe")
                executable.Value = "Minecraft.Windows.exe";
        }

        foreach (var ext in document.Root?.Elements(ns + "Extensions").ToList() ?? [])
            ext.Remove();

        foreach (var capability in document.Descendants(ns + "Capabilities").Elements(rescap + "Capability").Where(item => item.Attribute("Name")?.Value == "customInstallActions").ToList())
            capability.Remove();

        document.Save(path);
    }

    static async Task UnregisterFamilyAsync(string packageFamily, bool preserveApplicationData)
    {
        foreach (var package in s_manager.FindPackages(packageFamily))
        {
            var options = preserveApplicationData ? RemovalOptions.PreserveApplicationData | RemovalOptions.RemoveForAllUsers : RemovalOptions.RemoveForAllUsers;
            await RunDeploymentAsync(s_manager.RemovePackageAsync(package.Id.FullName, options), "remove", _ => { }, 0, 0);
        }
    }

    static async Task RegisterMaterializedInstallAsync(string installDirectory, PackageInstallKind kind, string packageFamily, IReadOnlyDictionary<string, PackageIdentityInfo>? availablePackages, string stagingDirectory, IList<string> transientDependencyPaths, Action<int> action)
    {
        var manifestPath = Path.Combine(installDirectory, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The extracted install is missing AppxManifest.xml.", manifestPath);

        IReadOnlyList<Uri> dependencyUris = [];

        if (kind == PackageInstallKind.Gdk)
        {
            var originalPath = Path.Combine(installDirectory, "AppxManifest_original.xml");
            if (!File.Exists(originalPath))
                File.Copy(manifestPath, originalPath, false);

            FixGdkManifest(manifestPath);
        }
        else if (availablePackages is not null)
        {
            dependencyUris = await ResolveAndInstallDependenciesAsync(manifestPath, availablePackages, stagingDirectory, transientDependencyPaths, action);
        }

        await UnregisterFamilyAsync(packageFamily, preserveApplicationData: true);
        action(90);
        await RunDeploymentAsync(s_manager.RegisterPackageAsync(new Uri(manifestPath), dependencyUris, DeploymentOptions.DevelopmentMode), "register", action, 90, 99);
        action(100);
    }

    static async Task MaterializeGdkAsync(string packagePath, string installDirectory, string version, Action<int> action)
    {
        await s_gdkInstallLock.WaitAsync();
        try
        {
            var packageFamily = GetPackageFamily(version);
            action(75);
            await UnregisterFamilyAsync(packageFamily, preserveApplicationData: false);
            await RunDeploymentAsync(s_manager.StagePackageAsync(new Uri(packagePath), null), "stage", action, 76, 84);

            string installPath = string.Empty;
            foreach (var package in new PackageManager().FindPackages(packageFamily))
            {
                if (installPath.Length > 0)
                    throw new InvalidOperationException("Minecraft is installed in multiple places and the launcher cannot determine which staged package to copy.");

                installPath = package.InstalledLocation.Path;
            }

            if (installPath.Length == 0)
                throw new DirectoryNotFoundException("The staged GDK package location could not be found.");

            installPath = ResolveFinalPath(installPath);
            var exeSourcePath = Path.Combine(installPath, "Minecraft.Windows.exe");
            if (!File.Exists(exeSourcePath))
                throw new FileNotFoundException("The staged Minecraft executable could not be found.", exeSourcePath);

            var tempExeDirectory = Path.Combine(s_stagingPath, "tmp");
            Directory.CreateDirectory(tempExeDirectory);
            var tempExePath = Path.Combine(tempExeDirectory, "Minecraft.Windows_" + Guid.NewGuid().ToString("N") + ".exe");
            var partialTempExePath = tempExePath + ".tmp";

            action(85);
            using (var shell = PowerShell.Create())
            {
                shell.AddCommand("Invoke-CommandInDesktopPackage");
                shell.AddParameter("PackageFamilyName", packageFamily);
                shell.AddParameter("AppId", "Game");
                shell.AddParameter("Command", "powershell.exe");
                shell.AddParameter("Args", $"-Command Copy-Item '{exeSourcePath}' '{partialTempExePath}' -Force; Move-Item '{partialTempExePath}' '{tempExePath}' -Force");
                shell.Invoke();
            }

            for (var index = 0; index < 300 && !File.Exists(tempExePath); index++)
                await Task.Delay(100);

            if (!File.Exists(tempExePath))
                throw new FileNotFoundException("The staged Minecraft executable could not be copied out of the package. Install Minecraft from Microsoft Store first so the license and decryption keys are available.", tempExePath);

            TryDeleteDirectory(installDirectory);
            action(88);

            if (Path.GetPathRoot(installPath).Equals(Path.GetPathRoot(installDirectory), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(installPath, installDirectory);
            }
            else
            {
                RecursiveCopyDirectory(installPath, installDirectory, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { exeSourcePath });
            }

            var exeDestinationPath = Path.Combine(installDirectory, "Minecraft.Windows.exe");
            if (File.Exists(exeDestinationPath))
                File.Delete(exeDestinationPath);

            File.Move(tempExePath, exeDestinationPath);

            if (!File.Exists(Path.Combine(installDirectory, "AppxManifest.xml")))
                throw new FileNotFoundException("The extracted GDK install is missing AppxManifest.xml.");

            if (!File.Exists(Path.Combine(installDirectory, GdkConfigFileName)))
                LogInstall("GDK install materialized without MicrosoftGame.Config", null, ("InstallDirectory", installDirectory));

            await UnregisterFamilyAsync(packageFamily, preserveApplicationData: true);
        }
        finally
        {
            s_gdkInstallLock.Release();
        }
    }

    static async Task MaterializeAsync(string[] uris, string version, string platform, PackageInstallKind kind, string stagingDirectory, Action<int> action)
    {
        var installDirectory = GetInstallDirectory(version, platform);
        Dictionary<string, PackageIdentityInfo>? availablePackages = null;
        List<string> transientDependencyPaths = [];
        string packagePath;

        if (kind == PackageInstallKind.Uwp)
        {
            availablePackages = await DownloadPackagesAsync(uris, stagingDirectory, kind, action);
            var mainPackageName = GetMainPackageName(version);
            packagePath = availablePackages
                .Where(_ => _.Value.Name.Equals(mainPackageName, StringComparison.OrdinalIgnoreCase) && !_.Value.IsFramework)
                .OrderByDescending(_ => _.Value.Version)
                .Select(_ => _.Key)
                .FirstOrDefault() ?? throw new InvalidOperationException($"The launcher could not identify the main package for {version}.");
        }
        else
        {
            packagePath = await DownloadPackageAsync(uris, stagingDirectory, kind, action);
        }

        try
        {
            if (kind == PackageInstallKind.Uwp)
                await InstallUwpPackageAsync(packagePath, GetPackageFamily(version), availablePackages!, stagingDirectory, transientDependencyPaths, action);
            else
                await MaterializeGdkAsync(packagePath, installDirectory, version, action);

            if (kind != PackageInstallKind.Uwp)
                await RegisterMaterializedInstallAsync(installDirectory, kind, GetPackageFamily(version), availablePackages, stagingDirectory, transientDependencyPaths, action);
        }
        finally
        {
            if (availablePackages is not null)
            {
                foreach (var downloadedPath in availablePackages.Keys)
                {
                    TryDeleteWithBackoff(downloadedPath);
                    s_paths.TryRemove(downloadedPath, out _);
                }
            }
            else
            {
                TryDeleteWithBackoff(packagePath);
                s_paths.TryRemove(packagePath, out _);
            }

            foreach (var dependencyPath in transientDependencyPaths)
            {
                TryDeleteWithBackoff(dependencyPath);
                s_paths.TryRemove(dependencyPath, out _);
            }
        }
    }

    readonly Task _task;
    readonly string _path;

    static bool ShouldPreserveStaging(Exception? exception)
        => exception is not null && IsLockedFileFailure(exception);

    internal InstallRequest(string[] uris, string version, string platform, PackageInstallKind kind, Action<int> action)
    {
        _path = Path.Combine(s_stagingPath, "flarial-switch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_path);
        s_paths.TryAdd(_path, new());

        _task = MaterializeAsync(uris, version, platform, kind, _path, action);
        _task.ContinueWith(delegate
        {
            var exception = _task.Exception?.GetBaseException();
            if (ShouldPreserveStaging(exception))
            {
                LogInstall("Preserving switcher staging directory for lock diagnosis", exception, ("StagingDirectory", _path));
            }
            else
            {
                TryDeleteDirectory(_path);
            }

            s_paths.TryRemove(_path, out _);
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    public TaskAwaiter GetAwaiter() => _task.GetAwaiter();

    ~InstallRequest()
    {
        if (!_task.IsFaulted || !ShouldPreserveStaging(_task.Exception?.GetBaseException()))
            TryDeleteDirectory(_path);

        s_paths.TryRemove(_path, out _);
    }
}
