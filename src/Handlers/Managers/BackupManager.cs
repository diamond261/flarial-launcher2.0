using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Flarial.Launcher.Services.Core;

namespace Flarial.Launcher.Managers;

public sealed class BackupMetadata
{
    public Guid BackupId { get; set; } = Guid.NewGuid();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string MinecraftVersion { get; set; } = string.Empty;
    public string SourcePlatform { get; set; } = string.Empty;
    public bool IsSafetyBackup { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public sealed class BackupOperationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string BackupName { get; set; } = string.Empty;

    public static BackupOperationResult Passed(string message, string backupName = "")
        => new() { Success = true, Message = message, BackupName = backupName };

    public static BackupOperationResult Failed(string message)
        => new() { Success = false, Message = message };
}

public sealed class BackupPathOverrides
{
    public string LegacyGamesPath { get; set; } = string.Empty;
    public string RoamingStatePath { get; set; } = string.Empty;
    public string LocalStatePath { get; set; } = string.Empty;
    public string MinecraftBedrockPath { get; set; } = string.Empty;
}

public class BackupConfiguration
{
    public DateTime BackupTime { get; set; }
    public string MinecraftVersion { get; set; }
    public Guid BackupId { get; set; }
}

internal sealed class BackupPlatformContext
{
    public string Platform { get; set; } = "UWP";
    public string ComMojangPath { get; set; } = string.Empty;
    public IReadOnlyList<string> AlternateComMojangPaths { get; set; } = Array.Empty<string>();
    public string GdkUserId { get; set; } = string.Empty;
}

internal static class BackupLayout
{
    public static readonly string[] Platforms = ["UWP", "GDK"];

    public static readonly string[] RequiredEntries =
    [
        "minecraftWorlds",
        "resource_packs",
        "behavior_packs",
        "development_behavior_packs",
        "development_resource_packs",
        "development_skin_packs",
        "minecraftpe"
    ];

    static readonly HashSet<string> s_requiredEntrySet = new(RequiredEntries, StringComparer.OrdinalIgnoreCase);

    public static bool IsRequiredEntry(string name)
        => s_requiredEntrySet.Contains(name);

    public static string GetPlatformBackupDirectory(string rootDirectory, string platform)
        => Path.Combine(rootDirectory, platform.Equals("GDK", StringComparison.OrdinalIgnoreCase) ? "GDK" : "UWP");
}

internal static class BackupPathResolver
{
    public static string ResolveBackupZipPath(string backupRoot, string backupName)
    {
        if (string.IsNullOrWhiteSpace(backupName))
            return string.Empty;

        var normalized = NormalizeRelativePath(backupName);
        if (normalized.Length == 0)
            return string.Empty;

        var normalizedLower = normalized.ToLowerInvariant();
        if (!normalizedLower.EndsWith(".zip"))
            normalized += ".zip";

        foreach (var platform in BackupLayout.Platforms)
        {
            var platformPrefix = platform + Path.DirectorySeparatorChar;
            if (!normalized.StartsWith(platformPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var platformRoot = Path.GetFullPath(BackupLayout.GetPlatformBackupDirectory(backupRoot, platform));
            var candidate = Path.GetFullPath(Path.Combine(backupRoot, normalized));
            if (!IsChildPath(platformRoot, candidate))
                return string.Empty;

            return candidate;
        }

        var fileName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        foreach (var platform in BackupLayout.Platforms)
        {
            var platformDirectory = BackupLayout.GetPlatformBackupDirectory(backupRoot, platform);
            var candidate = Path.Combine(platformDirectory, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return string.Empty;
    }

    public static bool IsZipBackupName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = NormalizeRelativePath(value);
        return normalized.Length > 0 && normalized.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    static string NormalizeRelativePath(string value)
    {
        var normalized = value.Trim()
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        if (normalized.Length == 0 || Path.IsPathRooted(normalized))
            return string.Empty;

        var segments = normalized.Split([Path.DirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment == "." || segment == ".."))
            return string.Empty;

        return string.Join(Path.DirectorySeparatorChar.ToString(), segments);
    }

    static bool IsChildPath(string rootPath, string candidatePath)
    {
        var root = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class VersionDetector
{
    static readonly string s_defaultUwpComMojangPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages",
        "Microsoft.MinecraftUWP_8wekyb3d8bbwe",
        "LocalState",
        "games",
        "com.mojang");

    static readonly string s_defaultGdkRootPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Minecraft Bedrock");

    public string DetectCurrentVersion()
    {
        try
        {
            var state = Minecraft.GetInstallState();
            if (!state.IsInstalled)
                return "UWP";

            return state.Platform.Equals("GDK", StringComparison.OrdinalIgnoreCase)
                ? "GDK"
                : "UWP";
        }
        catch
        {
            return "UWP";
        }
    }

    public string DetectCurrentMinecraftVersion()
    {
        try
        {
            return Minecraft.IsInstalled ? Minecraft.Version : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    internal BackupPlatformContext DetectCurrentContext(BackupPathOverrides overrides = null)
    {
        if (overrides is not null && !string.IsNullOrWhiteSpace(overrides.LocalStatePath))
        {
            return new BackupPlatformContext
            {
                Platform = "UWP",
                ComMojangPath = NormalizeComMojangPathFromLegacyOrComMojang(overrides.LegacyGamesPath, Path.Combine(overrides.LocalStatePath, "games", "com.mojang"))
            };
        }

        if (overrides is not null && !string.IsNullOrWhiteSpace(overrides.MinecraftBedrockPath))
            return DetectGdkContext(overrides.MinecraftBedrockPath);

        var installState = Minecraft.GetInstallState();
        if (installState.IsInstalled && installState.Platform.Equals("GDK", StringComparison.OrdinalIgnoreCase))
            return DetectGdkContext(s_defaultGdkRootPath);

        return new BackupPlatformContext
        {
            Platform = "UWP",
            ComMojangPath = s_defaultUwpComMojangPath
        };
    }

    static string NormalizeComMojangPathFromLegacyOrComMojang(string legacyOrComMojangPath, string fallbackComMojangPath)
    {
        if (!string.IsNullOrWhiteSpace(legacyOrComMojangPath))
        {
            var explicitComMojang = Path.Combine(legacyOrComMojangPath, "com.mojang");
            if (Directory.Exists(explicitComMojang))
                return explicitComMojang;

            return legacyOrComMojangPath;
        }

        return fallbackComMojangPath;
    }

    static BackupPlatformContext DetectGdkContext(string gdkRootPath)
    {
        var candidates = CollectGdkComMojangCandidates(gdkRootPath);
        if (candidates.Count == 0)
        {
            var fallback = Path.Combine(gdkRootPath, "com.mojang");
            return new BackupPlatformContext
            {
                Platform = "GDK",
                ComMojangPath = fallback,
                AlternateComMojangPaths = Array.Empty<string>()
            };
        }

        var ordered = candidates
            .OrderByDescending(static path => Directory.GetLastWriteTimeUtc(path))
            .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var primary = ordered[0];
        var primaryUser = TryExtractGdkUserId(gdkRootPath, primary);

        if (ordered.Count > 1)
            Logger.Info($"Backup detector found multiple GDK user data roots ({ordered.Count}); selected {primaryUser}.");

        return new BackupPlatformContext
        {
            Platform = "GDK",
            ComMojangPath = primary,
            AlternateComMojangPaths = ordered,
            GdkUserId = primaryUser
        };
    }

    static List<string> CollectGdkComMojangCandidates(string gdkRootPath)
    {
        List<string> candidates = [];

        var usersPath = Path.Combine(gdkRootPath, "Users");
        if (Directory.Exists(usersPath))
        {
            foreach (var userDirectory in Directory.GetDirectories(usersPath))
            {
                var candidateDirect = Path.Combine(userDirectory, "com.mojang");
                var candidateLegacy = Path.Combine(userDirectory, "games", "com.mojang");

                if (Directory.Exists(candidateDirect))
                    candidates.Add(candidateDirect);
                else if (Directory.Exists(candidateLegacy))
                    candidates.Add(candidateLegacy);
            }
        }

        var rootDirect = Path.Combine(gdkRootPath, "com.mojang");
        if (Directory.Exists(rootDirect))
            candidates.Add(rootDirect);

        var rootLegacy = Path.Combine(gdkRootPath, "games", "com.mojang");
        if (Directory.Exists(rootLegacy))
            candidates.Add(rootLegacy);

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string TryExtractGdkUserId(string gdkRootPath, string comMojangPath)
    {
        var usersPath = Path.Combine(gdkRootPath, "Users");
        if (!comMojangPath.StartsWith(usersPath, StringComparison.OrdinalIgnoreCase))
            return "root";

        var relative = comMojangPath.Substring(usersPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var first = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? "unknown" : first;
    }
}

public sealed class ZipService
{
    public void CreateZipBackup(string sourceDirectory, string zipPath)
    {
        if (File.Exists(zipPath))
            File.Delete(zipPath);

        ZipFile.CreateFromDirectory(sourceDirectory, zipPath, CompressionLevel.Optimal, false);
    }

    public void ExtractZipBackup(string zipPath, string destinationDirectory)
    {
        ValidateZipIntegrity(zipPath);

        if (Directory.Exists(destinationDirectory))
            Directory.Delete(destinationDirectory, true);

        Directory.CreateDirectory(destinationDirectory);
        var destinationFullPath = Path.GetFullPath(destinationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var entryPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(entryPath))
                throw new InvalidDataException("Zip contains rooted paths.");

            var targetPath = Path.GetFullPath(Path.Combine(destinationDirectory, entryPath));

            if (!targetPath.StartsWith(destinationFullPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Zip path traversal detected.");

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            using var source = entry.Open();
            using var destination = File.Create(targetPath);
            source.CopyTo(destination);
        }
    }

    public void ValidateZipIntegrity(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal)
                || normalized.Contains("../", StringComparison.Ordinal)
                || normalized.Contains("..\\", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Zip contains unsafe paths.");
            }

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                continue;

            using var stream = entry.Open();
            stream.CopyTo(Stream.Null);
        }
    }
}

public sealed class BackupService
{
    readonly Func<string> _backupRootResolver;
    readonly VersionDetector _versionDetector;
    readonly ZipService _zipService;

    public BackupService(Func<string> backupRootResolver, VersionDetector versionDetector, ZipService zipService)
        => (_backupRootResolver, _versionDetector, _zipService) = (backupRootResolver, versionDetector, zipService);

    public async Task<BackupOperationResult> BackupMCBEData(string backupName = null, BackupPathOverrides sourceOverrides = null, bool isSafetyBackup = false, string notes = "")
    {
        var stagingDirectory = string.Empty;

        try
        {
            var context = _versionDetector.DetectCurrentContext(sourceOverrides);
            stagingDirectory = Path.Combine(Path.GetTempPath(), "flarial-backup-stage-" + Guid.NewGuid().ToString("N"));
            var payloadDirectory = Path.Combine(stagingDirectory, "com.mojang");
            Directory.CreateDirectory(payloadDirectory);

            var sources = context.AlternateComMojangPaths.Count > 0
                ? context.AlternateComMojangPaths
                : new[] { context.ComMojangPath };

            var copiedEntries = 0;
            foreach (var source in sources.Where(Directory.Exists))
                copiedEntries += CopyRequiredEntries(source, payloadDirectory, overwriteExisting: false);

            if (copiedEntries == 0)
                return BackupOperationResult.Failed("No MCBE data was found to back up.");

            var metadata = new BackupMetadata
            {
                CreatedAtUtc = DateTime.UtcNow,
                MinecraftVersion = _versionDetector.DetectCurrentMinecraftVersion(),
                SourcePlatform = context.Platform,
                IsSafetyBackup = isSafetyBackup,
                Notes = notes
            };

            File.WriteAllText(Path.Combine(stagingDirectory, "metadata.json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

            var backupRoot = _backupRootResolver();
            var platformDirectory = BackupLayout.GetPlatformBackupDirectory(backupRoot, context.Platform);
            Directory.CreateDirectory(platformDirectory);

            var resolvedName = ResolveZipName(backupName, context.Platform, metadata.MinecraftVersion, isSafetyBackup);
            var destinationZipPath = Path.Combine(platformDirectory, resolvedName);

            await Task.Run(() => _zipService.CreateZipBackup(stagingDirectory, destinationZipPath));

            var relative = Path.Combine(context.Platform, resolvedName);
            Logger.Info($"Backup created | platform={context.Platform} | backup={relative}");
            return BackupOperationResult.Passed($"Backup created: {relative}", relative);
        }
        catch (Exception exception)
        {
            Logger.Error("Backup creation failed", exception);
            return BackupOperationResult.Failed($"Failed to create backup: {exception.Message}");
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    public string FindLatestBackup(string platform = "")
    {
        var backupRoot = _backupRootResolver();
        var files = new List<string>();

        if (!string.IsNullOrWhiteSpace(platform))
        {
            var scoped = BackupLayout.GetPlatformBackupDirectory(backupRoot, platform);
            if (Directory.Exists(scoped))
                files.AddRange(Directory.GetFiles(scoped, "*.zip", SearchOption.TopDirectoryOnly));
        }
        else
        {
            foreach (var directory in new[]
            {
                BackupLayout.GetPlatformBackupDirectory(backupRoot, "UWP"),
                BackupLayout.GetPlatformBackupDirectory(backupRoot, "GDK")
            })
            {
                if (Directory.Exists(directory))
                    files.AddRange(Directory.GetFiles(directory, "*.zip", SearchOption.TopDirectoryOnly));
            }
        }

        var latest = files
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

        if (latest is null)
            return string.Empty;

        var platformFolder = latest.Directory?.Name ?? "UWP";
        return Path.Combine(platformFolder, latest.Name);
    }

    static string ResolveZipName(string backupName, string platform, string minecraftVersion, bool isSafetyBackup)
    {
        if (!string.IsNullOrWhiteSpace(backupName))
        {
            var fileName = backupName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? backupName
                : backupName + ".zip";
            return SanitizeFileName(Path.GetFileName(fileName));
        }

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        if (isSafetyBackup)
            return SanitizeFileName($"{platform}_safety_{timestamp}.zip");

        return SanitizeFileName($"{platform}_backup_{timestamp}.zip");
    }

    internal static int CopyRequiredEntries(string sourceComMojangPath, string destinationComMojangPath, bool overwriteExisting)
    {
        if (!Directory.Exists(sourceComMojangPath))
            return 0;

        Directory.CreateDirectory(destinationComMojangPath);
        var copied = 0;

        foreach (var name in BackupLayout.RequiredEntries)
        {
            var sourcePath = Path.Combine(sourceComMojangPath, name);
            var destinationPath = Path.Combine(destinationComMojangPath, name);

            if (Directory.Exists(sourcePath))
            {
                if (overwriteExisting && Directory.Exists(destinationPath))
                    Directory.Delete(destinationPath, true);

                CopyDirectory(sourcePath, destinationPath, overwriteExisting);
                copied++;
            }
            else if (File.Exists(sourcePath))
            {
                if (overwriteExisting && File.Exists(destinationPath))
                    File.Delete(destinationPath);

                var parent = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(parent))
                    Directory.CreateDirectory(parent);

                File.Copy(sourcePath, destinationPath, overwriteExisting);
                copied++;
            }
        }

        return copied;
    }

    static void CopyDirectory(string sourceDirectory, string destinationDirectory, bool overwrite)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(file));
            File.Copy(file, destination, overwrite);
        }

        foreach (var directory in Directory.GetDirectories(sourceDirectory))
        {
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(directory));
            CopyDirectory(directory, destination, overwrite);
        }
    }

    static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.ToCharArray();

        for (var index = 0; index < chars.Length; index++)
        {
            if (invalid.Contains(chars[index]))
                chars[index] = '_';
        }

        return new string(chars);
    }

    internal static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        try { Directory.Delete(path, true); }
        catch { }
    }
}

public sealed class RestoreService
{
    readonly Func<string> _backupRootResolver;
    readonly VersionDetector _versionDetector;
    readonly ZipService _zipService;
    readonly BackupService _backupService;

    public RestoreService(Func<string> backupRootResolver, VersionDetector versionDetector, ZipService zipService, BackupService backupService)
        => (_backupRootResolver, _versionDetector, _zipService, _backupService) = (backupRootResolver, versionDetector, zipService, backupService);

    public async Task<BackupOperationResult> RestoreMCBEData(string backupName = null, BackupPathOverrides targetOverrides = null)
    {
        if (string.IsNullOrWhiteSpace(backupName))
            backupName = _backupService.FindLatestBackup();

        if (string.IsNullOrWhiteSpace(backupName))
            return BackupOperationResult.Failed("No backups were found to restore.");

        if (!BackupPathResolver.IsZipBackupName(backupName))
            return BackupOperationResult.Failed("Only ZIP backups are supported.");

        var extractionRoot = string.Empty;

        try
        {
            var backupZipPath = ResolveBackupPath(_backupRootResolver(), backupName);
            if (string.IsNullOrWhiteSpace(backupZipPath) || !File.Exists(backupZipPath))
                return BackupOperationResult.Failed("No Minecraft backups available with the given name.");

            if (targetOverrides is null)
            {
                var currentInstall = Minecraft.GetInstallState();
                if (!currentInstall.IsInstalled)
                    return BackupOperationResult.Failed("Restore requires an installed Minecraft package on the current platform.");
            }

            var context = _versionDetector.DetectCurrentContext(targetOverrides);
            if (string.IsNullOrWhiteSpace(context.ComMojangPath))
                return BackupOperationResult.Failed("Could not resolve current platform data path for restore.");

            if (HasAnyRequiredEntry(context.ComMojangPath))
            {
                var safetyResult = await _backupService.BackupMCBEData(
                    backupName: null,
                    sourceOverrides: targetOverrides,
                    isSafetyBackup: true,
                    notes: $"Pre-restore safety backup before loading {backupName}.");

                if (!safetyResult.Success)
                    return BackupOperationResult.Failed($"Restore aborted: unable to create safety backup. {safetyResult.Message}");
            }

            extractionRoot = Path.Combine(Path.GetTempPath(), "flarial-backup-restore-" + Guid.NewGuid().ToString("N"));
            await Task.Run(() => _zipService.ExtractZipBackup(backupZipPath, extractionRoot));

            var payloadRoot = ResolvePayloadComMojangRoot(extractionRoot);
            if (string.IsNullOrWhiteSpace(payloadRoot))
                return BackupOperationResult.Failed("Backup payload does not contain supported MCBE data.");

            Directory.CreateDirectory(context.ComMojangPath);
            BackupService.CopyRequiredEntries(payloadRoot, context.ComMojangPath, overwriteExisting: true);

            Logger.Info($"Backup restored | platform={context.Platform} | source={backupName}");
            return BackupOperationResult.Passed($"Backup loaded from {backupName}.", backupName);
        }
        catch (InvalidDataException invalidDataException)
        {
            Logger.Error("Backup restore failed due to invalid backup payload", invalidDataException);
            return BackupOperationResult.Failed($"Failed to load backup: {invalidDataException.Message}");
        }
        catch (Exception exception)
        {
            Logger.Error("Backup restore failed", exception);
            return BackupOperationResult.Failed($"Failed to load backup: {exception.Message}");
        }
        finally
        {
            BackupService.TryDeleteDirectory(extractionRoot);
        }
    }

    static string ResolvePayloadComMojangRoot(string extractionRoot)
    {
        var modernPath = Path.Combine(extractionRoot, "com.mojang");
        if (Directory.Exists(modernPath))
            return modernPath;

        var legacyPath = Path.Combine(extractionRoot, "LS", "games", "com.mojang");
        if (Directory.Exists(legacyPath))
            return legacyPath;

        return string.Empty;
    }

    static bool HasAnyRequiredEntry(string comMojangPath)
    {
        if (!Directory.Exists(comMojangPath))
            return false;

        return BackupLayout.RequiredEntries.Any(entry =>
            Directory.Exists(Path.Combine(comMojangPath, entry))
            || File.Exists(Path.Combine(comMojangPath, entry)));
    }

    static string ResolveBackupPath(string backupRoot, string backupName)
    {
        var path = BackupPathResolver.ResolveBackupZipPath(backupRoot, backupName);
        return File.Exists(path) ? path : string.Empty;
    }
}

public sealed class MCBEBackupManager
{
    readonly Func<string> _backupRootResolver;
    readonly VersionDetector _versionDetector;
    readonly BackupService _backupService;
    readonly RestoreService _restoreService;

    public MCBEBackupManager(Func<string> backupRootResolver)
    {
        _backupRootResolver = backupRootResolver;
        _versionDetector = new VersionDetector();
        var zipService = new ZipService();
        _backupService = new BackupService(_backupRootResolver, _versionDetector, zipService);
        _restoreService = new RestoreService(_backupRootResolver, _versionDetector, zipService, _backupService);
    }

    public string DetectCurrentVersion()
        => _versionDetector.DetectCurrentVersion();

    public Task<BackupOperationResult> BackupMCBEData(string backupName = null, BackupPathOverrides sourceOverrides = null, bool isSafetyBackup = false, string notes = "")
        => _backupService.BackupMCBEData(backupName, sourceOverrides, isSafetyBackup, notes);

    public Task<BackupOperationResult> RestoreMCBEData(string backupName = null, BackupPathOverrides targetOverrides = null)
        => _restoreService.RestoreMCBEData(backupName, targetOverrides);

    public string FindLatestBackup(string platform = "")
        => _backupService.FindLatestBackup(platform);

    public Task<string> FindLatestBackupAsync(string platform = "")
        => Task.FromResult(_backupService.FindLatestBackup(platform));

    public async Task<List<string>> GetAllBackupsAsync()
    {
        return await Task.Run(() =>
        {
            var root = _backupRootResolver();
            Directory.CreateDirectory(root);

            List<string> entries = [];
            foreach (var platform in new[] { "UWP", "GDK" })
            {
                var directory = BackupLayout.GetPlatformBackupDirectory(root, platform);
                if (!Directory.Exists(directory))
                    continue;

                var files = Directory.GetFiles(directory, "*.zip", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => Path.Combine(platform, name));
                entries.AddRange(files);
            }

            return entries
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });
    }

    public Task<BackupOperationResult> DeleteBackup(string backupName)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!BackupPathResolver.IsZipBackupName(backupName))
                    return BackupOperationResult.Failed("Only ZIP backups are supported.");

                var root = _backupRootResolver();
                var path = ResolveBackupPathForDelete(root, backupName);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return BackupOperationResult.Failed("Backup not found.");

                File.Delete(path);
                return BackupOperationResult.Passed($"Deleted backup '{backupName}'.", backupName);
            }
            catch (Exception exception)
            {
                Logger.Error("Backup delete failed", exception);
                return BackupOperationResult.Failed($"Failed to delete backup: {exception.Message}");
            }
        });
    }

    static string ResolveBackupPathForDelete(string backupRoot, string backupName)
    {
        var resolvedPath = BackupPathResolver.ResolveBackupZipPath(backupRoot, backupName);
        return File.Exists(resolvedPath) ? resolvedPath : string.Empty;
    }
}

public static class BackupManager
{
    static readonly MCBEBackupManager s_manager = new(() => backupDirectory);

    public static string backupDirectory = Path.Combine(VersionManagement.launcherPath, "launcher_data", "backups");

    public static string DetectCurrentVersion()
        => s_manager.DetectCurrentVersion();

    public static string DetectCurrentMinecraftVersion()
        => new VersionDetector().DetectCurrentMinecraftVersion();

    public static async Task<List<string>> FilterByName(string filterName)
    {
        var backups = await GetAllBackupsAsync();
        return backups.Where(backup => backup.Contains(filterName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static Task<List<string>> GetAllBackupsAsync()
        => s_manager.GetAllBackupsAsync();

    public static Task<BackupOperationResult> LoadBackup(string backupName)
        => s_manager.RestoreMCBEData(backupName);

    public static Task<BackupOperationResult> RestoreMCBEData(string backupName = null)
        => s_manager.RestoreMCBEData(backupName);

    public static Task<BackupOperationResult> LoadBackup(string backupName, BackupPathOverrides targetOverrides)
        => s_manager.RestoreMCBEData(backupName, targetOverrides);

    public static Task<BackupOperationResult> DeleteBackup(string backupName)
        => s_manager.DeleteBackup(backupName);

    public static async Task<string> CreateVersionSwitchBackupAsync()
    {
        var result = await s_manager.BackupMCBEData();
        if (!result.Success)
            throw new InvalidOperationException(result.Message);

        return result.BackupName;
    }

    public static async Task<bool> CreateBackup(string backupName)
        => (await s_manager.BackupMCBEData(backupName)).Success;

    public static Task<BackupOperationResult> CreateBackup(string backupName, BackupPathOverrides sourceOverrides)
        => s_manager.BackupMCBEData(backupName, sourceOverrides);

    public static string FindLatestBackup(string platform = "")
        => s_manager.FindLatestBackup(platform);

    public static Task<string> FindLatestBackupAsync(string platform = "")
        => s_manager.FindLatestBackupAsync(platform);

    public static string CreateZipBackup(string sourceDirectory, string zipPath)
    {
        var service = new ZipService();
        service.CreateZipBackup(sourceDirectory, zipPath);
        return zipPath;
    }

    public static string ExtractZipBackup(string zipPath, string destinationDirectory)
    {
        var service = new ZipService();
        service.ExtractZipBackup(zipPath, destinationDirectory);
        return destinationDirectory;
    }

    public static async Task<string> CreateConfig()
    {
        await Task.CompletedTask;

        var backupConfig = new BackupConfiguration
        {
            BackupTime = DateTime.Now,
            MinecraftVersion = MinecraftGame.GetVersion().ToString(),
            BackupId = Guid.NewGuid(),
        };

        return JsonSerializer.Serialize(backupConfig);
    }

    public static async Task<BackupConfiguration> GetConfig(string backupName)
    {
        await Task.CompletedTask;

        try
        {
            if (!BackupPathResolver.IsZipBackupName(backupName))
                return null;

            var root = BackupPathResolver.ResolveBackupZipPath(backupDirectory, backupName);
            if (File.Exists(root))
            {
                using var archive = ZipFile.OpenRead(root);
                var metadataEntry = archive.GetEntry("metadata.json");
                if (metadataEntry is null)
                    return null;

                using var stream = metadataEntry.Open();
                var metadata = await JsonSerializer.DeserializeAsync<BackupMetadata>(stream).ConfigureAwait(false);
                if (metadata is null)
                    return null;

                return new BackupConfiguration
                {
                    BackupId = metadata.BackupId,
                    BackupTime = metadata.CreatedAtUtc.ToLocalTime(),
                    MinecraftVersion = metadata.MinecraftVersion
                };
            }
        }
        catch { }

        return null;
    }
}
