using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services.Interfaces;

namespace GoogleDriveWorkSync.Services;

public class UpdateService : IUpdateService, IDisposable
{
    private const string GitHubRepoOwner = "AnaCataVC";
    private const string GitHubRepoName = "google-drive-work-sync";
    private static readonly string LatestReleaseApiUrl = $"https://api.github.com/repos/{GitHubRepoOwner}/{GitHubRepoName}/releases/latest";

    private readonly HttpClient _httpClient;
    private readonly string _currentVersion;

    public string CurrentAppVersion => _currentVersion;

    public UpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _currentVersion = ResolveCurrentVersion();
        ConfigureHttpClient();
    }

    private void ConfigureHttpClient()
    {
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("GoogleDriveWorkSync", _currentVersion));
        }
        if (!_httpClient.DefaultRequestHeaders.Contains("Accept"))
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        }
    }

    private static string ResolveCurrentVersion()
    {
        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null)
            {
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }
        }
        catch { }
        return "1.0.0";
    }

    public async Task<UpdateInfo> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var info = new UpdateInfo { CurrentVersion = _currentVersion };

        try
        {
            using var response = await _httpClient.GetAsync(LatestReleaseApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                info.ErrorMessage = $"GitHub API respondió con código: {(int)response.StatusCode}";
                return info;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? string.Empty : string.Empty;
            string htmlUrl = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? string.Empty : string.Empty;
            string body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? string.Empty : string.Empty;

            info.LatestVersion = NormalizeVersionString(tagName);
            info.ReleaseHtmlUrl = htmlUrl;
            info.ReleaseNotes = body;

            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    string assetName = asset.TryGetProperty("name", out var aName) ? aName.GetString() ?? string.Empty : string.Empty;
                    string downloadUrl = asset.TryGetProperty("browser_download_url", out var aUrl) ? aUrl.GetString() ?? string.Empty : string.Empty;

                    if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        info.InstallerDownloadUrl = downloadUrl;
                        break;
                    }
                }
            }

            return info;
        }
        catch (Exception ex)
        {
            info.ErrorMessage = $"Error al buscar actualizaciones: {ex.Message}";
            return info;
        }
    }

    public async Task<string> DownloadInstallerAsync(
        UpdateInfo updateInfo,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(updateInfo.InstallerDownloadUrl))
            throw new ArgumentException("No hay URL de instalador disponible.");

        var tempDir = Path.Combine(Path.GetTempPath(), "GoogleDriveWorkSync_Updates");
        Directory.CreateDirectory(tempDir);

        var destinationPath = Path.Combine(tempDir, $"GoogleDriveWorkSync-Setup-v{updateInfo.LatestVersion}.exe");

        using var response = await _httpClient.GetAsync(updateInfo.InstallerDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long totalBytesRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalBytesRead += bytesRead;

            if (totalBytes > 0 && progress != null)
            {
                int percentage = (int)Math.Round((double)totalBytesRead / totalBytes * 100.0);
                progress.Report(percentage);
            }
        }

        return destinationPath;
    }

    public bool LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath)) return false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true
            };
            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string NormalizeVersionString(string rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion)) return "0.0.0";
        var cleaned = rawVersion.Trim();
        if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[1..];
        }
        return cleaned;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
