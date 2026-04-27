using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using StartupGroups.Core.Branding;

namespace StartupGroups.App.Services;

public interface IUpdateInstaller
{
    Task<string> DownloadAsync(string url, IProgress<double>? progress, CancellationToken cancellationToken);
    void LaunchInstallerAndExit(string msiPath);
}

public sealed class UpdateInstaller : IUpdateInstaller
{
    private static readonly HttpClient Http = CreateHttpClient();
    private readonly ILogger<UpdateInstaller> _logger;

    public UpdateInstaller(ILogger<UpdateInstaller> logger)
    {
        _logger = logger;
    }

    public async Task<string> DownloadAsync(string url, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Installer URL is empty.", nameof(url));

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"StartupGroups-Update-{Guid.NewGuid():N}.msi");

        using var response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var src = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            if (totalBytes > 0)
            {
                progress?.Report((double)copied / totalBytes);
            }
        }

        _logger.LogInformation("Downloaded update MSI to {Path} ({Bytes} bytes).", tempPath, copied);
        return tempPath;
    }

    public void LaunchInstallerAndExit(string msiPath)
    {
        if (!File.Exists(msiPath))
            throw new FileNotFoundException("Installer not found.", msiPath);

        // msiexec triggers UAC; the running app must exit so MajorUpgrade can replace files.
        var startInfo = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = $"/i \"{msiPath}\"",
            UseShellExecute = true,
        };
        Process.Start(startInfo);
        _logger.LogInformation("Launched installer; shutting down to allow upgrade.");
        Application.Current.Shutdown();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(AppBranding.AppId, AppBranding.Version));
        return client;
    }
}
