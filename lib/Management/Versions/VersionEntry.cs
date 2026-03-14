using System;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;

namespace Flarial.Launcher.Services.Management.Versions;

public abstract class VersionEntry
{
    internal abstract Task<string> UriAsync();

    internal virtual async Task<string[]> UrisAsync() => [await UriAsync()];

    internal abstract InstallRequest.PackageInstallKind InstallKind { get; }

    public async Task<InstallRequest> InstallAsync(string version, string platform, Action<int> action)
        => new(await UrisAsync(), version, platform, InstallKind, action);

    private protected static readonly DataContractJsonSerializerSettings s_settings = new() { UseSimpleDictionaryFormat = true };
}
