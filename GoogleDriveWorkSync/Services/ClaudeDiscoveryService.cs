using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Helpers;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services.Interfaces;

namespace GoogleDriveWorkSync.Services;

public class ClaudeDiscoveryService : IClaudeDiscoveryService
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", ".vs", ".idea",
        ".vscode", "vendor", "packages", "dist", "build", "target", "out",
        "artifacts", "releases", ".next", ".nuxt", ".venv", "venv", "env",
        "__pycache__", ".pytest_cache", ".terraform", ".angular"
    };

    private static readonly HashSet<string> SensitiveNameKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "secret", "credential", "password", "token", "private_key", "id_rsa", "id_ed25519"
    };

    private static readonly List<Regex> InfrastructureSecretPatterns = new()
    {
        new Regex(@"-----BEGIN\s+(?:RSA|OPENSSH|DSA|EC|PGP)?\s*PRIVATE KEY-----", RegexOptions.Compiled),
        new Regex(@"(?:A3T[A-Z0-9]|AKIA|AGPA|AIDA|AROA|AIPA|ANPA|ANVA|ASIA)[A-Z0-9]{16}", RegexOptions.Compiled),
        new Regex(@"ghp_[a-zA-Z0-9]{36,255}", RegexOptions.Compiled),
        new Regex(@"gho_[a-zA-Z0-9]{36,255}", RegexOptions.Compiled),
        new Regex(@"github_pat_[a-zA-Z0-9]{22}_[a-zA-Z0-9]{59}", RegexOptions.Compiled),
        new Regex(@"xox[baprs]-[0-9]{10,13}-[0-9]{10,13}[a-zA-Z0-9-]*", RegexOptions.Compiled)
    };

    private static readonly List<Regex> McpValueSecretPatterns = new()
    {
        new Regex(@"Bearer\s+[A-Za-z0-9\-_\.=]{16,}", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"://[^/\s:@]+:[^/\s@]+@", RegexOptions.Compiled)
    };

    private static readonly HashSet<string> HookScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".sh", ".py", ".js", ".cmd", ".bat"
    };

    private static readonly string[] SensitiveMcpKeyFragments =
    {
        "token", "key", "secret", "password", "auth", "header", "env"
    };

    private readonly string _gitExecutable;
    private readonly string _homeDirectory;
    private readonly IDriveSyncService _driveSyncService;

    public ClaudeDiscoveryService(IDriveSyncService driveSyncService, string gitExecutable = "git", string? homeDirectory = null)
    {
        _driveSyncService = driveSyncService;
        _gitExecutable = gitExecutable;
        _homeDirectory = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public string BuildDriveRelativePath(ClaudeDiscoveryCandidate candidate)
    {
        string destinationPrefix = _driveSyncService.Settings.ClaudeDestinationPrefix;
        string noRepoBucket = _driveSyncService.Settings.ClaudeNoRepoBucketName;
        string claudeConfigBucket = _driveSyncService.Settings.ClaudeConfigBucketName;

        string projectSegment;
        if (!string.IsNullOrEmpty(candidate.RepositoryRoot))
        {
            projectSegment = Path.GetFileName(candidate.RepositoryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        else if (candidate.Category != ClaudeDiscoveryCategory.Context)
        {
            projectSegment = string.IsNullOrWhiteSpace(claudeConfigBucket) ? "_claude-config" : claudeConfigBucket.Trim();
        }
        else
        {
            projectSegment = string.IsNullOrWhiteSpace(noRepoBucket) ? "_sin-repo" : noRepoBucket.Trim();
        }

        string relative = candidate.RelativePath.Replace('\\', '/');
        string prefix = string.IsNullOrWhiteSpace(destinationPrefix) ? "claude-md-unversioned" : destinationPrefix.Trim('/');
        return $"{prefix}/{projectSegment}/{relative}";
    }

    public async Task<ClaudeDiscoveryReport> DiscoverAsync(string rootPath, int maxDepth = 4, CancellationToken cancellationToken = default)
    {
        var report = new ClaudeDiscoveryReport();
        if (!Directory.Exists(rootPath))
            return report;

        var repoCandidateMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var directCandidates = new List<string>();
        var categoryByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var explicitRelativePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var homeClaudeDir = Path.Combine(_homeDirectory, ".claude");
        var homeAlreadyCoveredByRoot = string.Equals(Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(_homeDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        if (!homeAlreadyCoveredByRoot && Directory.Exists(homeClaudeDir))
        {
            CollectFromDotClaudeDir(homeClaudeDir, directCandidates, categoryByPath, explicitRelativePath);
        }

        var homeClaudeJson = Path.Combine(_homeDirectory, ".claude.json");
        var sanitizedMcpConfigPath = Path.Combine(Path.GetDirectoryName(LocalSettingsHelper.SettingsFilePath) ?? Path.GetTempPath(), "mcp-config.sanitized.json");
        if (ExtractSanitizedMcpConfig(homeClaudeJson, sanitizedMcpConfigPath))
        {
            directCandidates.Add(sanitizedMcpConfigPath);
            categoryByPath[sanitizedMcpConfigPath] = ClaudeDiscoveryCategory.McpConfig;
            explicitRelativePath[sanitizedMcpConfigPath] = "mcp-config.sanitized.json";
        }

        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((rootPath, 0));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (currentDir, depth) = queue.Dequeue();

            try
            {
                var claudeFile = Path.Combine(currentDir, "CLAUDE.md");
                var hasRootClaudeFile = File.Exists(claudeFile);
                if (hasRootClaudeFile && IsCandidateAllowed(claudeFile))
                {
                    directCandidates.Add(claudeFile);
                }

                var dotClaudeDir = Path.Combine(currentDir, ".claude");
                var hasDotClaudeFile = File.Exists(Path.Combine(dotClaudeDir, "CLAUDE.md"));

                if (hasRootClaudeFile || hasDotClaudeFile)
                {
                    var refDir = Path.Combine(currentDir, "references");
                    if (Directory.Exists(refDir))
                    {
                        foreach (var f in SafeEnumerateFiles(refDir, "*.md"))
                        {
                            if (IsCandidateAllowed(f)) directCandidates.Add(f);
                        }
                    }
                }

                if (Directory.Exists(dotClaudeDir))
                {
                    CollectFromDotClaudeDir(dotClaudeDir, directCandidates, categoryByPath, explicitRelativePath);
                }
            }
            catch { }

            if (depth >= maxDepth)
                continue;

            try
            {
                foreach (var sub in Directory.GetDirectories(currentDir))
                {
                    var name = Path.GetFileName(sub);
                    // Skip directories whose name is in the blocklist.
                    if (IsDirectorySkipped(name))
                        continue;
                    // Skip subdirectories that are themselves git repository roots
                    // (standard repos have a .git directory; worktrees have a .git file).
                    // This prevents the BFS from descending into nested repos or linked
                    // worktrees and picking up versioned CLAUDE.md files as untracked.
                    if (IsGitRepoRoot(sub))
                        continue;
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch { }
        }

        foreach (var file in directCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileDir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(fileDir)) continue;

            string? repoRoot = await GetGitRepoRootAsync(fileDir, cancellationToken);
            if (!string.IsNullOrEmpty(repoRoot))
            {
                if (!repoCandidateMap.TryGetValue(repoRoot, out var list))
                {
                    list = new List<string>();
                    repoCandidateMap[repoRoot] = list;
                }
                list.Add(file);
            }
            else
            {
                var fi = new FileInfo(file);
                var candidate = new ClaudeDiscoveryCandidate
                {
                    FilePath = file,
                    RelativePath = explicitRelativePath.TryGetValue(file, out var relPath) ? relPath : Path.GetRelativePath(rootPath, file).Replace('\\', '/'),
                    RepositoryRoot = string.Empty,
                    Category = categoryByPath.TryGetValue(file, out var category) ? category : ClaudeDiscoveryCategory.Context,
                    IsTrackedByGit = false,
                    FileSizeBytes = fi.Exists ? fi.Length : 0,
                    LastModified = fi.Exists ? fi.LastWriteTime : DateTime.Now
                };

                // Classify with hash cache
                string drivePath = BuildDriveRelativePath(candidate);
                candidate.SyncStatus = _driveSyncService.EvaluateFileStatus(candidate.FilePath, drivePath);
                candidate.IsSelected = candidate.SyncStatus != CandidateSyncStatus.UpToDate;

                report.Candidates.Add(candidate);
            }
        }

        report.RepositoriesScanned = repoCandidateMap.Count;
        report.TotalCandidatesCount = directCandidates.Distinct(StringComparer.OrdinalIgnoreCase).Count();

        foreach (var kvp in repoCandidateMap)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repoRoot = kvp.Key;
            var files = kvp.Value;

            var relFiles = files.Select(f => Path.GetRelativePath(repoRoot, f)).ToList();
            var tracked = await GetTrackedFilesAsync(repoRoot, relFiles, cancellationToken);

            for (int i = 0; i < files.Count; i++)
            {
                var absPath = files[i];
                var relPath = relFiles[i];
                var isTracked = tracked.Contains(relPath.Replace('/', Path.DirectorySeparatorChar));
                if (isTracked) continue;

                var fi = new FileInfo(absPath);
                var candidate = new ClaudeDiscoveryCandidate
                {
                    FilePath = absPath,
                    RelativePath = relPath,
                    RepositoryRoot = repoRoot,
                    Category = categoryByPath.TryGetValue(absPath, out var candidateCategory) ? candidateCategory : ClaudeDiscoveryCategory.Context,
                    IsTrackedByGit = false,
                    FileSizeBytes = fi.Exists ? fi.Length : 0,
                    LastModified = fi.Exists ? fi.LastWriteTime : DateTime.Now
                };

                string drivePath = BuildDriveRelativePath(candidate);
                candidate.SyncStatus = _driveSyncService.EvaluateFileStatus(candidate.FilePath, drivePath);
                candidate.IsSelected = candidate.SyncStatus != CandidateSyncStatus.UpToDate;

                report.Candidates.Add(candidate);
            }
        }

        report.UntrackedCandidatesCount = report.Candidates.Count;
        return report;
    }

    public static bool IsDirectorySkipped(string dirName)
    {
        if (SkippedDirectories.Contains(dirName))
            return true;

        if (dirName.StartsWith("_backup_", StringComparison.OrdinalIgnoreCase) ||
            dirName.StartsWith("backup_", StringComparison.OrdinalIgnoreCase) ||
            dirName.Equals("memory", StringComparison.OrdinalIgnoreCase) ||
            dirName.Equals("plans", StringComparison.OrdinalIgnoreCase) ||
            dirName.Equals("security", StringComparison.OrdinalIgnoreCase) ||
            dirName.Equals("cache", StringComparison.OrdinalIgnoreCase) ||
            dirName.Equals("plugins", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if <paramref name="fullPath"/> is itself the root of a git repository
    /// (i.e. contains a <c>.git</c> directory OR a <c>.git</c> file, the latter being the
    /// case for git worktrees created via <c>git worktree add</c>).
    /// This is used during BFS traversal to prevent the scanner from descending into
    /// nested/sibling repositories and picking up versioned CLAUDE.md files.
    /// </summary>
    public static bool IsGitRepoRoot(string fullPath)
    {
        var gitEntry = Path.Combine(fullPath, ".git");
        // Standard repository: .git is a directory
        if (Directory.Exists(gitEntry))
            return true;
        // Linked worktree: .git is a plain text file starting with "gitdir:"
        if (File.Exists(gitEntry))
            return true;
        return false;
    }

    public static bool IsCandidateAllowed(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (!string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileName(filePath);
        foreach (var keyword in SensitiveNameKeywords)
        {
            if (fileName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (HasInfrastructureSecret(filePath))
            return false;

        return true;
    }

    public static bool IsHookScriptAllowed(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (!HookScriptExtensions.Contains(ext))
        {
            return false;
        }

        var fileName = Path.GetFileName(filePath);
        foreach (var keyword in SensitiveNameKeywords)
        {
            if (fileName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (HasInfrastructureSecret(filePath))
            return false;

        return true;
    }

    public static bool IsJsonConfigAllowed(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (!string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileName(filePath);
        foreach (var keyword in SensitiveNameKeywords)
        {
            if (fileName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (HasInfrastructureSecret(filePath))
            return false;

        return true;
    }

    public static bool HasInfrastructureSecret(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return false;

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var buffer = new char[65536];
            int read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0) return false;

            var content = new string(buffer, 0, read);
            return MatchesAnySecretPattern(content, InfrastructureSecretPatterns);
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesAnySecretPattern(string content, IEnumerable<Regex> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.IsMatch(content))
                return true;
        }
        return false;
    }

    private static void CollectFromDotClaudeDir(
        string dotClaudeDir,
        List<string> directCandidates,
        Dictionary<string, string> categoryByPath,
        Dictionary<string, string> explicitRelativePath)
    {
        var dotClaudeFile = Path.Combine(dotClaudeDir, "CLAUDE.md");
        if (File.Exists(dotClaudeFile) && IsCandidateAllowed(dotClaudeFile))
        {
            directCandidates.Add(dotClaudeFile);
        }

        var dotClaudeRefs = Path.Combine(dotClaudeDir, "references");
        if (Directory.Exists(dotClaudeRefs))
        {
            foreach (var f in SafeEnumerateFilesRecursive(dotClaudeRefs, 3))
            {
                if (IsCandidateAllowed(f)) directCandidates.Add(f);
            }
        }

        CollectCategoryFiles(dotClaudeDir, "skills", 3, ClaudeDiscoveryCategory.Skill, IsCandidateAllowed, directCandidates, categoryByPath, explicitRelativePath);
        CollectCategoryFiles(dotClaudeDir, "agents", 1, ClaudeDiscoveryCategory.Agent, IsCandidateAllowed, directCandidates, categoryByPath, explicitRelativePath);
        CollectCategoryFiles(dotClaudeDir, "scheduled-tasks", 3, ClaudeDiscoveryCategory.ScheduledTask, IsCandidateAllowed, directCandidates, categoryByPath, explicitRelativePath);
        CollectCategoryFiles(dotClaudeDir, "agent-memory", 3, ClaudeDiscoveryCategory.AgentMemory, IsCandidateAllowed, directCandidates, categoryByPath, explicitRelativePath);
        CollectCategoryFiles(dotClaudeDir, "hooks", 1, ClaudeDiscoveryCategory.Hook, IsHookScriptAllowed, directCandidates, categoryByPath, explicitRelativePath);

        CollectSingleFile(dotClaudeDir, "settings.json", ClaudeDiscoveryCategory.GlobalSetting, IsJsonConfigAllowed, directCandidates, categoryByPath, explicitRelativePath);
        CollectSingleFile(dotClaudeDir, "settings.local.json", ClaudeDiscoveryCategory.GlobalSetting, IsJsonConfigAllowed, directCandidates, categoryByPath, explicitRelativePath);
        CollectSingleFile(dotClaudeDir, "keybindings.json", ClaudeDiscoveryCategory.Keybinding, IsJsonConfigAllowed, directCandidates, categoryByPath, explicitRelativePath);

        var projectsDir = Path.Combine(dotClaudeDir, "projects");
        if (Directory.Exists(projectsDir))
        {
            foreach (var projDir in Directory.GetDirectories(projectsDir))
            {
                var projMemoryDir = Path.Combine(projDir, "memory");
                if (Directory.Exists(projMemoryDir))
                {
                    foreach (var f in SafeEnumerateFilesRecursive(projMemoryDir, 2))
                    {
                        if (!IsCandidateAllowed(f)) continue;
                        directCandidates.Add(f);
                        categoryByPath[f] = ClaudeDiscoveryCategory.ProjectMemory;
                        explicitRelativePath[f] = Path.GetRelativePath(dotClaudeDir, f).Replace('\\', '/');
                    }
                }
            }
        }
    }

    public static bool ExtractSanitizedMcpConfig(string claudeJsonPath, string outputPath)
    {
        try
        {
            if (!File.Exists(claudeJsonPath)) return false;

            using var stream = File.OpenRead(claudeJsonPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            var sanitized = new JsonObject();

            if (root.TryGetProperty("mcpServers", out var globalServers) && globalServers.ValueKind == JsonValueKind.Object)
            {
                sanitized["mcpServers"] = SanitizeMcpJsonNode(JsonNode.Parse(globalServers.GetRawText()));
            }

            if (root.TryGetProperty("projects", out var projects) && projects.ValueKind == JsonValueKind.Object)
            {
                var perProjectServers = new JsonObject();
                foreach (var project in projects.EnumerateObject())
                {
                    if (project.Value.TryGetProperty("mcpServers", out var projServers) &&
                        projServers.ValueKind == JsonValueKind.Object &&
                        projServers.EnumerateObject().Any())
                    {
                        perProjectServers[project.Name] = SanitizeMcpJsonNode(JsonNode.Parse(projServers.GetRawText()));
                    }
                }

                if (perProjectServers.Count > 0)
                {
                    sanitized["projectMcpServers"] = perProjectServers;
                }
            }

            if (sanitized.Count == 0) return false;

            string json = sanitized.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            if (MatchesAnySecretPattern(json, InfrastructureSecretPatterns) || MatchesAnySecretPattern(json, McpValueSecretPatterns))
            {
                return false;
            }

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(outputPath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static JsonNode? SanitizeMcpJsonNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var resultObj = new JsonObject();
                foreach (var property in obj)
                {
                    if (IsSensitiveMcpKey(property.Key))
                    {
                        resultObj[property.Key] = "[REDACTED]";
                    }
                    else
                    {
                        resultObj[property.Key] = SanitizeMcpJsonNode(property.Value?.DeepClone());
                    }
                }
                return resultObj;

            case JsonArray array:
                var resultArray = new JsonArray();
                foreach (var item in array)
                {
                    resultArray.Add(SanitizeMcpJsonNode(item?.DeepClone()));
                }
                return resultArray;

            default:
                if (node is JsonValue value && value.TryGetValue(out string? stringValue) && stringValue != null &&
                    (MatchesAnySecretPattern(stringValue, InfrastructureSecretPatterns) || MatchesAnySecretPattern(stringValue, McpValueSecretPatterns)))
                {
                    return "[REDACTED]";
                }
                return node?.DeepClone();
        }
    }

    private static bool IsSensitiveMcpKey(string key)
    {
        foreach (var fragment in SensitiveMcpKeyFragments)
        {
            if (key.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void CollectSingleFile(
        string dotClaudeDir,
        string fileName,
        string category,
        Func<string, bool> isAllowed,
        List<string> directCandidates,
        Dictionary<string, string> categoryByPath,
        Dictionary<string, string> explicitRelativePath)
    {
        var filePath = Path.Combine(dotClaudeDir, fileName);
        if (!File.Exists(filePath) || !isAllowed(filePath)) return;

        directCandidates.Add(filePath);
        categoryByPath[filePath] = category;
        explicitRelativePath[filePath] = fileName;
    }

    private static void CollectCategoryFiles(
        string dotClaudeDir,
        string subfolderName,
        int maxDepth,
        string category,
        Func<string, bool> isAllowed,
        List<string> directCandidates,
        Dictionary<string, string> categoryByPath,
        Dictionary<string, string> explicitRelativePath)
    {
        var folder = Path.Combine(dotClaudeDir, subfolderName);
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var f in SafeEnumerateFilesRecursive(folder, maxDepth))
        {
            if (!isAllowed(f)) continue;

            directCandidates.Add(f);
            categoryByPath[f] = category;
            explicitRelativePath[f] = Path.GetRelativePath(dotClaudeDir, f).Replace('\\', '/');
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dirPath, string searchPattern)
    {
        try
        {
            return Directory.GetFiles(dirPath, searchPattern, SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEnumerateFilesRecursive(string rootDir, int maxDepth)
    {
        var results = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((rootDir, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            try
            {
                foreach (var file in Directory.GetFiles(current))
                {
                    results.Add(file);
                }
            }
            catch { }

            if (depth >= maxDepth) continue;

            try
            {
                foreach (var sub in Directory.GetDirectories(current))
                {
                    var dirInfo = new DirectoryInfo(sub);
                    if (!dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) && !IsDirectorySkipped(dirInfo.Name))
                    {
                        queue.Enqueue((sub, depth + 1));
                    }
                }
            }
            catch { }
        }

        return results;
    }

    public async Task<string?> GetGitRepoRootAsync(string directory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _gitExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(directory);
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("--show-toplevel");

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null) return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            await stderrTask;

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                var repoRoot = stdout.Trim().Replace('/', Path.DirectorySeparatorChar);
                return Directory.Exists(repoRoot) ? repoRoot : Path.GetFullPath(repoRoot);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<HashSet<string>> GetTrackedFilesAsync(
        string repoRoot,
        IEnumerable<string> relativeFilePaths,
        CancellationToken cancellationToken)
    {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileList = relativeFilePaths.ToList();
        if (fileList.Count == 0) return tracked;

        const int batchSize = 50;
        for (int i = 0; i < fileList.Count; i += batchSize)
        {
            var batch = fileList.Skip(i).Take(batchSize).ToList();
            var startInfo = new ProcessStartInfo
            {
                FileName = _gitExecutable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            // Without quotePath=false git prints non-ASCII paths as octal escapes, which never
            // match the requested path and make tracked files look untracked.
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("core.quotePath=false");
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(repoRoot);
            startInfo.ArgumentList.Add("ls-files");
            startInfo.ArgumentList.Add("--");
            foreach (var relPath in batch)
            {
                startInfo.ArgumentList.Add(relPath.Replace('\\', '/'));
            }

            try
            {
                using var process = Process.Start(startInfo);
                if (process == null) continue;

                var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                // Fail-open on purpose: when git cannot answer, files are treated as untracked and
                // get backed up, since an extra upload is cheaper than silently skipping a file.
                if (process.ExitCode != 0)
                {
                    DiagnosticLogger.LogCrash("ClaudeDiscoveryService_GetTrackedFiles", null,
                        $"git ls-files falló en {repoRoot} (exit {process.ExitCode}): {stderr.Trim()}");
                }
                else if (!string.IsNullOrWhiteSpace(stdout))
                {
                    using var reader = new StringReader(stdout);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            var normalized = line.Trim().Replace('/', Path.DirectorySeparatorChar);
                            tracked.Add(normalized);
                        }
                    }
                }
            }
            catch { }
        }

        return tracked;
    }
}
