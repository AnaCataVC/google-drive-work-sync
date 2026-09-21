using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Models;

namespace GoogleDriveWorkSync.Services.Interfaces;

public interface IClaudeDiscoveryService
{
    Task<ClaudeDiscoveryReport> DiscoverAsync(string rootPath, int maxDepth = 4, CancellationToken cancellationToken = default);
    string BuildDriveRelativePath(ClaudeDiscoveryCandidate candidate);
}
