using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Helpers;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services;
using Moq;
using Xunit;

namespace GoogleDriveWorkSync.Tests;

public class GlobPatternMatcherTests
{
    [Theory]
    [InlineData("*.psd", "art/cover.psd", false, true)]
    [InlineData("*.psd", "cover.PSD", false, true)]
    [InlineData("*.psd", "cover.psd.txt", false, false)]
    [InlineData("drafts", "a/b/drafts/x.txt", false, true)]
    [InlineData("drafts", "a/drafts", false, true)]
    [InlineData("/drafts", "drafts/x.txt", false, true)]
    [InlineData("/drafts", "a/drafts/x.txt", false, false)]
    [InlineData("docs/*.md", "docs/a.md", false, true)]
    [InlineData("docs/*.md", "x/docs/a.md", false, false)]
    [InlineData("docs/*.md", "docs/sub/a.md", false, false)]
    [InlineData("**/out/*.pdf", "out/a.pdf", false, true)]
    [InlineData("**/out/*.pdf", "a/b/out/a.pdf", false, true)]
    [InlineData("**/out/*.pdf", "a/out/sub/a.pdf", false, false)]
    [InlineData("logs/**", "logs/a/b.txt", false, true)]
    [InlineData("a/**/z.txt", "a/z.txt", false, true)]
    [InlineData("a/**/z.txt", "a/b/c/z.txt", false, true)]
    [InlineData("file?.txt", "file1.txt", false, true)]
    [InlineData("file?.txt", "file12.txt", false, false)]
    [InlineData("build/", "build", true, true)]
    [InlineData("build/", "build", false, false)]
    [InlineData("build/", "src/build/app.dll", false, true)]
    public void IsExcluded_FollowsGitignoreSubset(string pattern, string path, bool isDirectory, bool expected)
    {
        var matcher = GlobPatternMatcher.Parse(pattern);
        Assert.Equal(expected, matcher.IsExcluded(path, isDirectory));
    }

    [Fact]
    public void Parse_IgnoresCommentsBlankLinesAndNegation()
    {
        var matcher = GlobPatternMatcher.Parse("# *.txt, *.md\n\n   \n!keep.log\n*.log");

        Assert.False(matcher.IsExcluded("notes.txt", false));
        Assert.False(matcher.IsExcluded("readme.md", false));
        // Negation is unsupported: the line is dropped, so it cannot re-include keep.log.
        Assert.True(matcher.IsExcluded("keep.log", false));
    }

    [Fact]
    public void Parse_AcceptsCommasAndNewlines()
    {
        var matcher = GlobPatternMatcher.Parse("*.psd, *.ai\r\n/tmp/");

        Assert.True(matcher.IsExcluded("a.psd", false));
        Assert.True(matcher.IsExcluded("a.ai", false));
        Assert.True(matcher.IsExcluded("tmp/x", false));
        Assert.False(matcher.IsExcluded("a.png", false));
    }

    [Fact]
    public void Parse_OnlyNegationOrComments_IsEmpty()
    {
        Assert.True(GlobPatternMatcher.Parse("# c\n!x", null, "").IsEmpty);
        Assert.False(GlobPatternMatcher.Empty.IsExcluded("anything", false));
    }
}

public class ScanFolderGlobExclusionTests : IDisposable
{
    private readonly string _testDir;
    private readonly TempSettingsFileScope _settingsScope;
    private readonly DriveSyncService _service;

    public ScanFolderGlobExclusionTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "GoogleDriveWorkSync_GlobTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _settingsScope = new TempSettingsFileScope(Path.Combine(_testDir, "test_settings.json"));
        _service = new DriveSyncService();
    }

    public void Dispose()
    {
        _service.Dispose();
        _settingsScope.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    private string Touch(string relativePath)
    {
        var full = Path.Combine(_testDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, relativePath);
        return full;
    }

    private static SyncFilterOptions NoFilters() => SyncFilterOptions.Create("", "", "", 25);

    [Fact]
    public void ScanFolder_PrunesExcludedDirectoriesAndFiles_FromSettings()
    {
        Touch("keep/a.txt");
        Touch("keep/art.psd");
        Touch("drafts/b.txt");
        Touch("nested/drafts/c.txt");
        Touch("cache/d.txt");

        var settings = _service.Settings;
        settings.ExcludedPatterns = "*.psd\n/drafts/\ncache";
        _service.UpdateSettings(settings);

        var names = _service.ScanFolder(_testDir, NoFilters()).Select(f => f.RelativePath.Replace('\\', '/')).ToList();

        Assert.Contains("keep/a.txt", names);
        Assert.Contains("nested/drafts/c.txt", names);
        Assert.DoesNotContain("keep/art.psd", names);
        Assert.DoesNotContain("drafts/b.txt", names);
        Assert.DoesNotContain("cache/d.txt", names);
    }

    [Fact]
    public void ScanFolder_MergesSyncIgnoreAtRoot()
    {
        Touch("a.txt");
        Touch("secret/b.txt");
        Touch("out/c.bin");
        File.WriteAllText(Path.Combine(_testDir, GlobPatternMatcher.IgnoreFileName), "# local rules\nsecret/\n**/*.bin\n");

        var names = _service.ScanFolder(_testDir, NoFilters()).Select(f => f.RelativePath.Replace('\\', '/')).ToList();

        Assert.Contains("a.txt", names);
        Assert.DoesNotContain("secret/b.txt", names);
        Assert.DoesNotContain("out/c.bin", names);
    }

    [Fact]
    public async Task DetectOrphans_IgnoresFilesNowExcluded()
    {
        var keptPath = Path.Combine(_testDir, "gone.txt");
        var excludedPath = Path.Combine(_testDir, "drafts", "gone.txt");

        var settings = _service.Settings;
        settings.Sources = new() { new SyncSource { LocalFolderPath = _testDir, DestinationPrefix = "work" } };
        settings.ExcludedPatterns = "drafts/";
        _service.UpdateSettings(settings);

        _service.SaveKnownHash($"work|{keptPath}", "h1", fileId: "id1", driveRelativePath: "work/gone.txt", localFilePath: keptPath);
        _service.SaveKnownHash($"work|{excludedPath}", "h2", fileId: "id2", driveRelativePath: "work/drafts/gone.txt", localFilePath: excludedPath);

        var orphans = await _service.DetectOrphansAsync();

        Assert.Single(orphans);
        Assert.Equal("work/gone.txt", orphans[0].DriveRelativePath);
    }
}

public class ClaudeDiscoveryGlobExclusionTests : IDisposable
{
    private readonly string _testDir;

    public ClaudeDiscoveryGlobExclusionTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "GoogleDriveWorkSync_ClaudeGlob_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task DiscoverAsync_SkipsExcludedDirectoriesRelativeToTarget()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, "keep"));
        Directory.CreateDirectory(Path.Combine(_testDir, "archive", "old"));
        File.WriteAllText(Path.Combine(_testDir, "keep", "CLAUDE.md"), "keep");
        File.WriteAllText(Path.Combine(_testDir, "archive", "old", "CLAUDE.md"), "old");

        var settings = new DriveSyncSettings { ExcludedPatterns = "/archive/" };
        var driveMock = new Mock<GoogleDriveWorkSync.Services.Interfaces.IDriveSyncService>();
        driveMock.SetupGet(d => d.Settings).Returns(settings);
        driveMock.Setup(d => d.EvaluateFileStatus(It.IsAny<string>(), It.IsAny<string>())).Returns(CandidateSyncStatus.New);

        var service = new ClaudeDiscoveryService(driveMock.Object, gitExecutable: "git", homeDirectory: _testDir);
        var report = await service.DiscoverAsync(_testDir, maxDepth: 3);

        Assert.Contains(report.Candidates, c => c.FilePath.EndsWith(Path.Combine("keep", "CLAUDE.md")));
        Assert.DoesNotContain(report.Candidates, c => c.FilePath.Contains(Path.Combine("archive", "old")));
    }
}
