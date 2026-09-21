using System;
using System.Threading;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Models;

namespace GoogleDriveWorkSync.Services.Interfaces;

public interface IUpdateService
{
    string CurrentAppVersion { get; }
    Task<UpdateInfo> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
    Task<string> DownloadInstallerAsync(UpdateInfo updateInfo, IProgress<int>? progress = null, CancellationToken cancellationToken = default);
    bool LaunchInstaller(string installerPath);
}
