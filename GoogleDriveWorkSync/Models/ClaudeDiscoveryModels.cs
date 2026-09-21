using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace GoogleDriveWorkSync.Models;

public static class ClaudeDiscoveryCategory
{
    public const string Context = "Contexto (CLAUDE.md)";
    public const string Skill = "Skill";
    public const string Agent = "Agente";
    public const string ScheduledTask = "Tarea Programada";
    public const string Hook = "Hook";
    public const string AgentMemory = "Memoria de Agente";
    public const string ProjectMemory = "Memoria de Proyecto";
    public const string GlobalSetting = "Configuración Global";
    public const string Keybinding = "Atajos de Teclado";
    public const string McpConfig = "Configuración MCP";

    public static readonly string[] DisplayOrder =
    {
        Context, Skill, Agent, ScheduledTask, Hook,
        AgentMemory, ProjectMemory, GlobalSetting, Keybinding, McpConfig
    };
}

public enum CandidateSyncStatus
{
    New,         // Never synced before or hash not found
    Modified,    // Content changed since last sync
    UpToDate,    // Synced and unchanged
    GitTracked   // Excluded by Git tracking
}

public class ClaudeDiscoveryCandidate : INotifyPropertyChanged
{
    public string FilePath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string RepositoryRoot { get; set; } = string.Empty;
    public string Category { get; set; } = ClaudeDiscoveryCategory.Context;
    public bool IsTrackedByGit { get; set; }
    public long FileSizeBytes { get; set; }
    public DateTime LastModified { get; set; }

    public CandidateSyncStatus SyncStatus { get; set; } = CandidateSyncStatus.New;

    public string SizeDisplay => OutOfSyncFile.FormatBytes(FileSizeBytes);

    public string StatusBadge => IsTrackedByGit
        ? "Git Tracked"
        : SyncStatus switch
        {
            CandidateSyncStatus.New => "Nuevo",
            CandidateSyncStatus.Modified => "Modificado",
            CandidateSyncStatus.UpToDate => "Al día",
            _ => "Pendiente"
        };

    private bool _isSelected = true;

    /// <summary>Whether this file is selected for synchronization.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class CandidateGroup : ObservableCollection<ClaudeDiscoveryCandidate>
{
    public string Category { get; }

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public CandidateGroup(string category, IEnumerable<ClaudeDiscoveryCandidate> items) : base(items)
    {
        Category = category;
    }

    public static List<CandidateGroup> BuildFrom(IEnumerable<ClaudeDiscoveryCandidate> candidates)
    {
        var byCategory = candidates.ToLookup(c => c.Category);
        var groups = new List<CandidateGroup>();
        foreach (var category in ClaudeDiscoveryCategory.DisplayOrder)
        {
            if (byCategory[category].Any())
            {
                groups.Add(new CandidateGroup(category, byCategory[category]));
            }
        }
        return groups;
    }
}

public class CategoryFilterOption : INotifyPropertyChanged
{
    public string Category { get; }

    private bool _isChecked = true;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public CategoryFilterOption(string category)
    {
        Category = category;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class ClaudeDiscoveryReport
{
    public List<ClaudeDiscoveryCandidate> Candidates { get; set; } = new();
    public int RepositoriesScanned { get; set; }
    public int UntrackedCandidatesCount { get; set; }
    public int TotalCandidatesCount { get; set; }
    public int OutOfSyncCount => Candidates.Count(c => !c.IsTrackedByGit && c.SyncStatus != CandidateSyncStatus.UpToDate);
    public DateTime ScannedAt { get; set; } = DateTime.Now;
}
