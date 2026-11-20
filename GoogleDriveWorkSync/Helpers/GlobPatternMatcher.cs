using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GoogleDriveWorkSync.Helpers;

/// <summary>
/// Matches forward-slash paths relative to a scan root against a gitignore subset:
/// <c>#</c> comments, <c>*</c>, <c>**</c>, <c>?</c>, trailing <c>/</c> (directories only) and
/// leading <c>/</c> (anchored at the root). A pattern without a slash matches the name of any
/// path segment. Negation (<c>!</c>) is not supported and such lines are ignored, so a pattern
/// can only exclude, never re-include. Matching is case-insensitive, like the Windows file system.
/// </summary>
public sealed class GlobPatternMatcher
{
    public const string IgnoreFileName = ".syncignore";

    private readonly List<CompiledPattern> _patterns;

    private sealed record CompiledPattern(Regex Regex, bool DirectoryOnly, bool MatchesName);

    public static GlobPatternMatcher Empty { get; } = new(new List<CompiledPattern>());

    private GlobPatternMatcher(List<CompiledPattern> patterns) => _patterns = patterns;

    public bool IsEmpty => _patterns.Count == 0;

    /// <summary>Builds a matcher from one or more pattern texts (newline- or comma-separated).</summary>
    public static GlobPatternMatcher Parse(params string?[] patternTexts)
    {
        var compiled = new List<CompiledPattern>();
        foreach (var text in patternTexts)
        {
            foreach (var line in SplitPatterns(text))
            {
                var pattern = Compile(line);
                if (pattern != null) compiled.Add(pattern);
            }
        }
        return compiled.Count == 0 ? Empty : new GlobPatternMatcher(compiled);
    }

    /// <summary>
    /// Builds the matcher for a work-source root: the configured patterns plus the optional
    /// <c>.syncignore</c> at that root. An unreadable ignore file is skipped rather than failing the scan.
    /// </summary>
    public static GlobPatternMatcher ForRoot(string? configuredPatterns, string? rootFolderPath)
    {
        string? ignoreFileText = null;
        if (!string.IsNullOrWhiteSpace(rootFolderPath))
        {
            var ignoreFile = Path.Combine(rootFolderPath, IgnoreFileName);
            try
            {
                if (File.Exists(ignoreFile)) ignoreFileText = File.ReadAllText(ignoreFile);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogCrash("GlobPatternMatcher_ForRoot", ex, $"No se pudo leer {IgnoreFileName}: {ignoreFile}");
            }
        }
        return Parse(configuredPatterns, ignoreFileText);
    }

    public static IEnumerable<string> SplitPatterns(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            // Comment lines are dropped whole so commas inside them are not read as separators.
            if (line.Length == 0 || line.StartsWith('#')) continue;

            foreach (var part in line.Split(','))
            {
                var pattern = part.Trim();
                if (pattern.Length == 0 || pattern.StartsWith('#') || pattern.StartsWith('!')) continue;
                yield return pattern;
            }
        }
    }

    /// <summary>
    /// True when the path, or any directory above it, is excluded. Checking the ancestors makes the
    /// answer independent of whether the caller pruned directories while walking.
    /// </summary>
    public bool IsExcluded(string relativePath, bool isDirectory)
    {
        if (IsEmpty || string.IsNullOrEmpty(relativePath)) return false;

        var segments = relativePath.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var prefix = new StringBuilder();
        for (int i = 0; i < segments.Length; i++)
        {
            if (i > 0) prefix.Append('/');
            prefix.Append(segments[i]);
            bool segmentIsDirectory = i < segments.Length - 1 || isDirectory;
            if (MatchesSingle(prefix.ToString(), segments[i], segmentIsDirectory)) return true;
        }
        return false;
    }

    private bool MatchesSingle(string path, string name, bool isDirectory)
    {
        foreach (var pattern in _patterns)
        {
            if (pattern.DirectoryOnly && !isDirectory) continue;
            if (pattern.Regex.IsMatch(pattern.MatchesName ? name : path)) return true;
        }
        return false;
    }

    private static CompiledPattern? Compile(string pattern)
    {
        var body = pattern.Replace('\\', '/');
        bool directoryOnly = body.EndsWith('/');
        body = body.TrimEnd('/');
        bool anchored = body.StartsWith('/');
        body = body.TrimStart('/');
        if (body.Length == 0) return null;

        // gitignore: a slash at the start or in the middle anchors the pattern to the root.
        bool matchesName = !anchored && !body.Contains('/');
        var regex = new Regex(
            "^" + GlobToRegex(body) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        return new CompiledPattern(regex, directoryOnly, matchesName);
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*')
            {
                bool doubleStar = i + 1 < glob.Length && glob[i + 1] == '*';
                if (!doubleStar)
                {
                    sb.Append("[^/]*");
                    continue;
                }

                i++;
                bool followedBySlash = i + 1 < glob.Length && glob[i + 1] == '/';
                if (followedBySlash)
                {
                    // "**/" matches zero or more whole directories.
                    sb.Append("(?:.*/)?");
                    i++;
                }
                else
                {
                    sb.Append(".*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        return sb.ToString();
    }
}
