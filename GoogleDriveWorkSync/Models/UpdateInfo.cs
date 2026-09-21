namespace GoogleDriveWorkSync.Models;

public class UpdateInfo
{
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string ReleaseHtmlUrl { get; set; } = string.Empty;
    public string InstallerDownloadUrl { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }

    public bool IsUpdateAvailable
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LatestVersion) || string.IsNullOrWhiteSpace(CurrentVersion))
                return false;

            if (System.Version.TryParse(CurrentVersion, out var curr) &&
                System.Version.TryParse(LatestVersion, out var latest))
            {
                return latest > curr;
            }

            return false;
        }
    }
}
