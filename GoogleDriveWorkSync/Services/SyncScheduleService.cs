using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Helpers;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services.Interfaces;

namespace GoogleDriveWorkSync.Services;

/// <summary>
/// Precision schedule service using System.Threading.Timer to evaluate every 30-60 seconds
/// if background auto-sync should fire for configured days and time.
/// </summary>
public class SyncScheduleService : ISyncScheduleService, IDisposable
{
    private const string ScheduleSettingsKey = "SyncScheduleSettings";
    private readonly Timer _timer;
    private ScheduleSettings _settings;
    private int _isRunning;

    public ScheduleSettings Settings => _settings;
    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    public event EventHandler? SettingsChanged;
    public event EventHandler? ScheduledSyncTriggered;

    public SyncScheduleService()
    {
        _settings = LoadSettings();
        // Check every 30 seconds
        _timer = new Timer(OnTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_settings.IsEnabled)
        {
            Start();
        }
    }

    public void UpdateSettings(ScheduleSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        SaveSettings();
        SettingsChanged?.Invoke(this, EventArgs.Empty);

        if (_settings.IsEnabled)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    public void Start()
    {
        Volatile.Write(ref _isRunning, 1);
        _timer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    }

    public void Stop()
    {
        Volatile.Write(ref _isRunning, 0);
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void OnTimerTick(object? state)
    {
        if (!IsRunning || !_settings.IsEnabled) return;

        var now = DateTime.Now;
        if (_settings.ShouldRunAt(now))
        {
            _settings.LastAutoSyncDate = now.Date;
            SaveSettings();

            ScheduledSyncTriggered?.Invoke(this, EventArgs.Empty);
        }
    }

    private static ScheduleSettings LoadSettings()
    {
        return LocalSettingsHelper.LoadJson<ScheduleSettings>(ScheduleSettingsKey);
    }

    private void SaveSettings()
    {
        LocalSettingsHelper.SaveJson(ScheduleSettingsKey, _settings);
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
