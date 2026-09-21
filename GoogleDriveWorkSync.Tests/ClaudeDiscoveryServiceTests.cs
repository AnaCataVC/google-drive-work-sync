using System;
using System.IO;
using System.Linq;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services;
using GoogleDriveWorkSync.Services.Interfaces;
using Moq;
using Xunit;

namespace GoogleDriveWorkSync.Tests;

public class ClaudeDiscoveryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fakeHomeDir;
    private readonly Mock<IDriveSyncService> _driveSyncMock;

    public ClaudeDiscoveryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ClaudeDiscoveryTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _fakeHomeDir = Path.Combine(Path.GetTempPath(), "ClaudeDiscoveryTests_FakeHome_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fakeHomeDir);

        _driveSyncMock = new Mock<IDriveSyncService>();
        _driveSyncMock.Setup(d => d.Settings).Returns(new DriveSyncSettings());
        _driveSyncMock.Setup(d => d.EvaluateFileStatus(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(CandidateSyncStatus.New);
    }

    private ClaudeDiscoveryService CreateService() => new(_driveSyncMock.Object, "git", _fakeHomeDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
        if (Directory.Exists(_fakeHomeDir))
        {
            try { Directory.Delete(_fakeHomeDir, true); } catch { }
        }
    }

    [Theory]
    [InlineData(".git", true)]
    [InlineData("node_modules", true)]
    [InlineData("memory", true)]
    [InlineData("plans", true)]
    [InlineData("security", true)]
    [InlineData("cache", true)]
    [InlineData("plugins", true)]
    [InlineData("_backup_2026", true)]
    [InlineData("backup_old", true)]
    [InlineData("src", false)]
    [InlineData("references", false)]
    public void IsDirectorySkipped_IdentifiesExcludedDirectories(string dirName, bool expected)
    {
        bool result = ClaudeDiscoveryService.IsDirectorySkipped(dirName);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void HasInfrastructureSecret_DetectsPrivateKey()
    {
        string filePath = Path.Combine(_tempDir, "key.md");
        File.WriteAllText(filePath, "# Config\n-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA...\n-----END RSA PRIVATE KEY-----");

        Assert.True(ClaudeDiscoveryService.HasInfrastructureSecret(filePath));
        Assert.False(ClaudeDiscoveryService.IsCandidateAllowed(filePath));
    }

    [Fact]
    public void HasInfrastructureSecret_DetectsGitHubPAT()
    {
        string filePath = Path.Combine(_tempDir, "pat.md");
        File.WriteAllText(filePath, "token: ghp_123456789012345678901234567890123456");

        Assert.True(ClaudeDiscoveryService.HasInfrastructureSecret(filePath));
        Assert.False(ClaudeDiscoveryService.IsCandidateAllowed(filePath));
    }

    [Fact]
    public void IsCandidateAllowed_AllowsCleanMarkdownFile()
    {
        string filePath = Path.Combine(_tempDir, "CLAUDE.md");
        File.WriteAllText(filePath, "# System Prompt\nFollow coding rules.");

        Assert.False(ClaudeDiscoveryService.HasInfrastructureSecret(filePath));
        Assert.True(ClaudeDiscoveryService.IsCandidateAllowed(filePath));
    }

    [Fact]
    public async System.Threading.Tasks.Task DiscoverAsync_IgnoresReferencesFolderWithoutClaudeMarker()
    {
        string otherToolDir = Path.Combine(_tempDir, "gemini-project");
        Directory.CreateDirectory(Path.Combine(otherToolDir, "references"));
        File.WriteAllText(Path.Combine(otherToolDir, "references", "gemini-notes.md"), "# Gemini notes");

        string claudeDir = Path.Combine(_tempDir, "claude-project");
        Directory.CreateDirectory(Path.Combine(claudeDir, "references"));
        File.WriteAllText(Path.Combine(claudeDir, "CLAUDE.md"), "# Claude context");
        File.WriteAllText(Path.Combine(claudeDir, "references", "claude-notes.md"), "# Claude notes");

        var service = CreateService();
        var report = await service.DiscoverAsync(_tempDir, maxDepth: 3);

        Assert.Contains(report.Candidates, c => c.FilePath.EndsWith("claude-notes.md"));
        Assert.DoesNotContain(report.Candidates, c => c.FilePath.EndsWith("gemini-notes.md"));
    }

    [Fact]
    public async System.Threading.Tasks.Task DiscoverAsync_FindsSkillsAgentsScheduledTasksAndHooks()
    {
        string dotClaudeDir = Path.Combine(_tempDir, ".claude");

        Directory.CreateDirectory(Path.Combine(dotClaudeDir, "skills", "my-skill"));
        File.WriteAllText(Path.Combine(dotClaudeDir, "skills", "my-skill", "SKILL.md"), "# My Skill");

        Directory.CreateDirectory(Path.Combine(dotClaudeDir, "agents"));
        File.WriteAllText(Path.Combine(dotClaudeDir, "agents", "my-agent.md"), "# My Agent");

        Directory.CreateDirectory(Path.Combine(dotClaudeDir, "scheduled-tasks", "my-task"));
        File.WriteAllText(Path.Combine(dotClaudeDir, "scheduled-tasks", "my-task", "SKILL.md"), "# My Task");

        Directory.CreateDirectory(Path.Combine(dotClaudeDir, "hooks"));
        File.WriteAllText(Path.Combine(dotClaudeDir, "hooks", "my-hook.ps1"), "Write-Host 'hi'");
        File.WriteAllText(Path.Combine(dotClaudeDir, "hooks", "state.json"), "{}");

        Directory.CreateDirectory(Path.Combine(dotClaudeDir, "agent-memory", "qa-tester"));
        File.WriteAllText(Path.Combine(dotClaudeDir, "agent-memory", "qa-tester", "MEMORY.md"), "# QA Tester Memory");

        Directory.CreateDirectory(Path.Combine(dotClaudeDir, "projects", "my-project", "memory"));
        File.WriteAllText(Path.Combine(dotClaudeDir, "projects", "my-project", "memory", "MEMORY.md"), "# Project Memory");

        var service = CreateService();
        var report = await service.DiscoverAsync(_tempDir, maxDepth: 3);

        var skill = Assert.Single(report.Candidates, c => c.Category == ClaudeDiscoveryCategory.Skill);
        Assert.Equal("skills/my-skill/SKILL.md", skill.RelativePath);

        var agent = Assert.Single(report.Candidates, c => c.Category == ClaudeDiscoveryCategory.Agent);
        Assert.Equal("agents/my-agent.md", agent.RelativePath);

        var scheduledTask = Assert.Single(report.Candidates, c => c.Category == ClaudeDiscoveryCategory.ScheduledTask);
        Assert.Equal("scheduled-tasks/my-task/SKILL.md", scheduledTask.RelativePath);

        var hook = Assert.Single(report.Candidates, c => c.Category == ClaudeDiscoveryCategory.Hook);
        Assert.Equal("hooks/my-hook.ps1", hook.RelativePath);

        var agentMemory = Assert.Single(report.Candidates, c => c.Category == ClaudeDiscoveryCategory.AgentMemory);
        Assert.Equal("agent-memory/qa-tester/MEMORY.md", agentMemory.RelativePath);

        var projectMemory = Assert.Single(report.Candidates, c => c.Category == ClaudeDiscoveryCategory.ProjectMemory);
        Assert.Equal("projects/my-project/memory/MEMORY.md", projectMemory.RelativePath);

        Assert.DoesNotContain(report.Candidates, c => c.FilePath.EndsWith("state.json"));
    }

    [Theory]
    [InlineData("settings.json", true)]
    [InlineData("mcp-secret.json", false)]
    [InlineData("notes.md", false)]
    public void IsJsonConfigAllowed_FiltersByExtensionAndName(string fileName, bool expected)
    {
        string filePath = Path.Combine(_tempDir, fileName);
        File.WriteAllText(filePath, "{}");

        Assert.Equal(expected, ClaudeDiscoveryService.IsJsonConfigAllowed(filePath));
    }

    [Fact]
    public void IsJsonConfigAllowed_RejectsFileWithInfrastructureSecret()
    {
        string filePath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(filePath, "{\"token\": \"ghp_123456789012345678901234567890123456\"}");

        Assert.False(ClaudeDiscoveryService.IsJsonConfigAllowed(filePath));
    }

    [Fact]
    public void ExtractSanitizedMcpConfig_RedactsSecretShapedValuesAndKeepsSafeOnes()
    {
        string sourcePath = Path.Combine(_tempDir, ".claude.json");
        string outputPath = Path.Combine(_tempDir, "mcp-config.sanitized.json");
        File.WriteAllText(sourcePath, """
        {
          "oauthAccount": { "id": "should-not-appear" },
          "mcpServers": {
            "my-server": {
              "command": "npx",
              "args": ["my-mcp-server"],
              "env": { "API_KEY": "sk-live-should-be-redacted" }
            }
          }
        }
        """);

        bool result = ClaudeDiscoveryService.ExtractSanitizedMcpConfig(sourcePath, outputPath);

        Assert.True(result);
        string sanitized = File.ReadAllText(outputPath);
        Assert.Contains("\"command\": \"npx\"", sanitized);
        Assert.Contains("[REDACTED]", sanitized);
        Assert.DoesNotContain("sk-live-should-be-redacted", sanitized);
        Assert.DoesNotContain("should-not-appear", sanitized);
    }
}
