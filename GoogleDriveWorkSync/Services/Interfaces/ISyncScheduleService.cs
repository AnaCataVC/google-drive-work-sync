using System;
using System.Threading;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Models;

namespace GoogleDriveWorkSync.Services.Interfaces;

public interface ISyncScheduleService
{
    ScheduleSettings Settings { get; }
    bool IsRunning { get; }

    event EventHandler? SettingsChanged;
    event EventHandler? ScheduledSyncTriggered;

    void UpdateSettings(ScheduleSettings settings);
    void Start();
    void Stop();
}
