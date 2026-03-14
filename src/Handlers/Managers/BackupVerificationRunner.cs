using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace Flarial.Launcher.Managers;

internal static class BackupVerificationRunner
{
    public static int Run(string scenario)
    {
        var normalized = string.IsNullOrWhiteSpace(scenario) ? "all" : scenario.Trim().ToLowerInvariant();
        return normalized switch
        {
            "all" => RunAll(),
            "create-delete" => RunScenario("create-delete", VerifyCreateDeleteAsync),
            "zip-restore" => RunScenario("zip-restore", VerifyZipRestoreAsync),
            "zip-slip" => RunScenario("zip-slip", VerifyZipSlipAsync),
            "gdk" => RunScenario("gdk", VerifyGdkRestoreAsync),
            "missing" => RunScenario("missing", VerifyMissingAsync),
            _ => Fail($"BACKUP_VERIFY scenario={normalized} result=invalid message=UnknownScenario")
        };
    }

    static int RunAll()
    {
        var results = new[]
        {
            RunScenario("create-delete", VerifyCreateDeleteAsync),
            RunScenario("zip-restore", VerifyZipRestoreAsync),
            RunScenario("zip-slip", VerifyZipSlipAsync),
            RunScenario("gdk", VerifyGdkRestoreAsync),
            RunScenario("missing", VerifyMissingAsync)
        };
        var failed = results.Any(result => result != 0);
        Log($"BACKUP_VERIFY scenario=all result={(failed ? "fail" : "pass")}");
        return failed ? 1 : 0;
    }

    static int RunScenario(string scenario, Func<Task> action)
    {
        try { action().GetAwaiter().GetResult(); Log($"BACKUP_VERIFY scenario={scenario} result=pass"); return 0; }
        catch (Exception ex) { return Fail($"BACKUP_VERIFY scenario={scenario} result=fail message={ex.Message.Replace(' ', '_')}"); }
    }

    static int Fail(string message)
    {
        Logger.Error(message, new InvalidOperationException(message));
        try { Console.WriteLine(message); } catch { }
        return message.Contains("result=invalid", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    }

    static void Log(string message)
    {
        Logger.Info(message);
        try { Console.WriteLine(message); } catch { }
    }

    static async Task VerifyCreateDeleteAsync()
    {
        await WithEnvironmentAsync(async e =>
        {
            SeedFile(e.Source.LocalStatePath, "games\\com.mojang\\minecraftWorlds\\slot.txt", "ok");
            SeedFile(e.Source.LocalStatePath, "games\\com.mojang\\minecraftpe", "ok");
            var create = await BackupManager.CreateBackup("manual-backup", e.Source);
            Assert(create.Success, "CreateShouldSucceed");
            Assert(create.BackupName.StartsWith("UWP" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "CreateShouldUseUwpFolder");
            var list = await BackupManager.GetAllBackupsAsync();
            Assert(list.Contains(create.BackupName, StringComparer.OrdinalIgnoreCase), "BackupShouldBeListed");
            var delete = await BackupManager.DeleteBackup(create.BackupName);
            Assert(delete.Success, "DeleteShouldSucceed");
        });
    }

    static async Task VerifyZipRestoreAsync()
    {
        await WithEnvironmentAsync(async e =>
        {
            SeedFile(e.Source.LocalStatePath, "games\\com.mojang\\minecraftWorlds\\w\\level.dat", "data");
            SeedFile(e.Source.LocalStatePath, "games\\com.mojang\\minecraftpe", "options");
            var created = await BackupManager.CreateBackup("restore-case", e.Source);
            Assert(created.Success, "CreateRestoreCaseFailed");
            var result = await BackupManager.LoadBackup(created.BackupName, e.Target);
            Assert(result.Success, "RestoreShouldSucceed");
            Assert(File.Exists(Path.Combine(e.Target.LocalStatePath, "games", "com.mojang", "minecraftWorlds", "w", "level.dat")), "WorldNotRestored");
        });
    }

    static async Task VerifyZipSlipAsync()
    {
        await WithEnvironmentAsync(async e =>
        {
            await Task.CompletedTask;

            var platformDirectory = Path.Combine(e.BackupRoot, "UWP");
            Directory.CreateDirectory(platformDirectory);

            var zipPath = Path.Combine(platformDirectory, "unsafe.zip");
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../outside.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("unsafe");
            }

            var restore = await BackupManager.LoadBackup(Path.Combine("UWP", "unsafe.zip"), e.Target);
            Assert(!restore.Success, "ZipSlipShouldFail");
        });
    }

    static async Task VerifyGdkRestoreAsync()
    {
        await WithEnvironmentAsync(async e =>
        {
            var source = new BackupPathOverrides { MinecraftBedrockPath = e.Source.MinecraftBedrockPath };
            var target = new BackupPathOverrides { MinecraftBedrockPath = e.Target.MinecraftBedrockPath };

            SeedFile(e.Source.MinecraftBedrockPath, "Users\\111\\com.mojang\\minecraftWorlds\\slot1\\level.dat", "one");
            SeedFile(e.Source.MinecraftBedrockPath, "Users\\222\\com.mojang\\minecraftWorlds\\slot2\\level.dat", "two");
            SeedFile(e.Target.MinecraftBedrockPath, "Users\\333\\com.mojang\\minecraftWorlds\\existing\\level.dat", "existing");

            var create = await BackupManager.CreateBackup("gdk-case", source);
            Assert(create.Success, "GdkCreateShouldSucceed");
            Assert(create.BackupName.StartsWith("GDK" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "GdkCreateShouldUseGdkFolder");

            var restore = await BackupManager.LoadBackup(create.BackupName, target);
            Assert(restore.Success, "GdkRestoreShouldSucceed");

            var destinationWorldRoot = Path.Combine(e.Target.MinecraftBedrockPath, "Users", "333", "com.mojang", "minecraftWorlds");
            Assert(Directory.Exists(destinationWorldRoot), "GdkDestinationMissing");
        });
    }

    static async Task VerifyMissingAsync()
    {
        await WithEnvironmentAsync(async e =>
        {
            var load = await BackupManager.LoadBackup("missing.zip", e.Target);
            Assert(!load.Success, "MissingLoadShouldFail");
        });
    }

    static async Task WithEnvironmentAsync(Func<BackupPathSet, Task> action)
    {
        var original = BackupManager.backupDirectory;
        var root = Path.Combine(Path.GetTempPath(), "flarial-backup-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            var env = new BackupPathSet(root);
            BackupManager.backupDirectory = env.BackupRoot;
            Directory.CreateDirectory(env.BackupRoot);
            await action(env);
        }
        finally
        {
            BackupManager.backupDirectory = original;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    static void SeedFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    sealed class BackupPathSet
    {
        public BackupPathSet(string root)
        {
            BackupRoot = Path.Combine(root, "backups");
            Source = new BackupPathOverrides
            {
                LocalStatePath = Path.Combine(root, "Source", "LocalState"),
                MinecraftBedrockPath = Path.Combine(root, "Source", "MinecraftBedrock")
            };
            Target = new BackupPathOverrides
            {
                LocalStatePath = Path.Combine(root, "Target", "LocalState"),
                MinecraftBedrockPath = Path.Combine(root, "Target", "MinecraftBedrock")
            };
        }
        public string BackupRoot { get; }
        public BackupPathOverrides Source { get; }
        public BackupPathOverrides Target { get; }
    }
}
