using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using static System.Environment;
using System.Net;
using MihaZupan;

namespace Flarial.Launcher.Services.Networking;

public class HttpService
{
    public const string DownloadPipelineRevision = "download-promote-retry-v3";
    static readonly HttpClient s_proxy = new(new HttpServiceHandler
    {
        Proxy = new HttpToSocks5Proxy($"{IPAddress.Loopback}", ushort.MaxValue)
    }, true);

    static readonly HttpClient s_client = new(new HttpServiceHandler(), true);

    static HttpClient HttpClient => UseProxy ? s_proxy : s_client;

    static readonly int s_length = SystemPageSize;

    public static bool UseProxy { get; set; }

    public static bool UseDnsOverHttps { get => HttpServiceHandler.UseDnsOverHttps; set => HttpServiceHandler.UseDnsOverHttps = value; }

    internal static async Task<HttpResponseMessage> GetAsync(string uri) => await HttpClient.GetAsync(uri);

    internal static async Task<HttpResponseMessage> PostAsync(string uri, HttpContent content) => await HttpClient.PostAsync(uri, content);

    internal static async Task<T> GetAsync<T>(string uri)
    {
        return (T)(object)(typeof(T) switch
        {
            var @_ when _ == typeof(string) => await HttpClient.GetStringAsync(uri),
            var @_ when _ == typeof(Stream) => await HttpClient.GetStreamAsync(uri),
            _ => throw new NotImplementedException()
        });
    }

    internal static async Task DownloadAsync(string uri, string path, Action<int>? action)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = $"{path}.{Path.GetRandomFileName()}.download";
        static bool IsLockLike(Exception exception)
            => exception is IOException or UnauthorizedAccessException;

        static async Task PromoteDownloadedFileAsync(string temporaryPath, string destinationPath)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    if (File.Exists(destinationPath))
                    {
                        var backupPath = $"{destinationPath}.{Path.GetRandomFileName()}.bak";
                        try
                        {
                            File.Replace(temporaryPath, destinationPath, backupPath, true);
                        }
                        finally
                        {
                            if (File.Exists(backupPath))
                            {
                                try { File.Delete(backupPath); }
                                catch { }
                            }
                        }
                    }
                    else
                    {
                        File.Move(temporaryPath, destinationPath);
                    }

                    return;
                }
                catch (Exception exception) when (attempt < 19 && IsLockLike(exception))
                {
                    await Task.Delay(Math.Min(1000 + (attempt * 500), 5000));
                }
            }

            if (File.Exists(destinationPath))
            {
                var backupPath = $"{destinationPath}.{Path.GetRandomFileName()}.bak";
                try
                {
                    File.Replace(temporaryPath, destinationPath, backupPath, true);
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    throw new IOException($"Downloaded file could not replace '{destinationPath}'. The destination may be locked by another process.", exception);
                }
            }
            else
            {
                try
                {
                    File.Move(temporaryPath, destinationPath);
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    throw new IOException($"Downloaded file could not move into '{destinationPath}'. The staged package path may be locked by another process.", exception);
                }
            }
        }

        try
        {
            using var message = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            message.EnsureSuccessStatusCode();

            {
                using var destination = File.Create(temporaryPath);
                using var source = await message.Content.ReadAsStreamAsync();

                int count = 0;
                long value = 0;
                var buffer = new byte[s_length];
                var length = message.Content.Headers.ContentLength ?? 0;

                while ((count = await source.ReadAsync(buffer, 0, s_length)) != 0)
                {
                    await destination.WriteAsync(buffer, 0, count);
                    value += count;

                    if (action is { } && length > 0)
                        action((int)(value * 100 / length));
                }

                await destination.FlushAsync();

                if (length > 0 && value < length)
                    throw new IOException($"Incomplete download: expected {length} bytes, received {value} bytes.");
            }

            await PromoteDownloadedFileAsync(temporaryPath, path);
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch { }

            throw;
        }
    }

    const string Uri = "https://cdn.flarial.xyz/202.txt";

    public static async Task<bool> IsAvailableAsync()
    {
        try { _ = await HttpClient.GetStringAsync(Uri); return true; }
        catch { return false; }
    }
}
