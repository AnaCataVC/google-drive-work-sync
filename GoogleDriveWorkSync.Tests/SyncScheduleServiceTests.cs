using System;
using System.Collections.Generic;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services;
using Xunit;

namespace GoogleDriveWorkSync.Tests;

public class SyncScheduleServiceTests
{
    [Fact]
    public void ShouldRunAt_WhenDisabled_ReturnsFalse()
    {
        var settings = new ScheduleSettings
        {
            IsEnabled = false,
            ScheduledTime = new TimeSpan(14, 0, 0),
            ScheduledDays = new List<DayOfWeek> { DayOfWeek.Monday }
        };

        var now = new DateTime(2026, 9, 21, 14, 30, 0); // Monday 14:30
        Assert.False(settings.ShouldRunAt(now));
    }

    [Fact]
    public void ShouldRunAt_WhenDayNotScheduled_ReturnsFalse()
    {
        var settings = new ScheduleSettings
        {
            IsEnabled = true,
            ScheduledTime = new TimeSpan(14, 0, 0),
            ScheduledDays = new List<DayOfWeek> { DayOfWeek.Tuesday, DayOfWeek.Wednesday }
        };

        var now = new DateTime(2026, 9, 21, 14, 30, 0); // Monday
        Assert.False(settings.ShouldRunAt(now));
    }

    [Fact]
    public void ShouldRunAt_WhenBeforeScheduledTime_ReturnsFalse()
    {
        var settings = new ScheduleSettings
        {
            IsEnabled = true,
            ScheduledTime = new TimeSpan(18, 0, 0),
            ScheduledDays = new List<DayOfWeek> { DayOfWeek.Monday }
        };

        var now = new DateTime(2026, 9, 21, 17, 59, 0); // Monday 17:59
        Assert.False(settings.ShouldRunAt(now));
    }

    [Fact]
    public void ShouldRunAt_WhenAtOrAfterScheduledTime_ReturnsTrue()
    {
        var settings = new ScheduleSettings
        {
            IsEnabled = true,
            ScheduledTime = new TimeSpan(18, 0, 0),
            ScheduledDays = new List<DayOfWeek> { DayOfWeek.Monday }
        };

        var nowAt = new DateTime(2026, 9, 21, 18, 0, 0); // Monday 18:00
        Assert.True(settings.ShouldRunAt(nowAt));

        var nowAfter = new DateTime(2026, 9, 21, 18, 30, 0); // Monday 18:30
        Assert.True(settings.ShouldRunAt(nowAfter));
    }

    [Fact]
    public void ShouldRunAt_WhenAlreadyRanToday_ReturnsFalse()
    {
        var today = new DateTime(2026, 9, 21, 18, 30, 0);
        var settings = new ScheduleSettings
        {
            IsEnabled = true,
            ScheduledTime = new TimeSpan(18, 0, 0),
            ScheduledDays = new List<DayOfWeek> { DayOfWeek.Monday },
            LastAutoSyncDate = today.Date
        };

        Assert.False(settings.ShouldRunAt(today));
    }

    [Fact]
    public void ShouldRunAt_WhenRanYesterday_ReturnsTrueToday()
    {
        var today = new DateTime(2026, 9, 21, 18, 30, 0);
        var settings = new ScheduleSettings
        {
            IsEnabled = true,
            ScheduledTime = new TimeSpan(18, 0, 0),
            ScheduledDays = new List<DayOfWeek> { DayOfWeek.Monday },
            LastAutoSyncDate = today.Date.AddDays(-1)
        };

        Assert.True(settings.ShouldRunAt(today));
    }

    [Fact]
    public void SyncScheduleService_StartAndStop_UpdatesIsRunning()
    {
        using var tempScope = new TempSettingsFileScope();
        using var service = new SyncScheduleService();

        service.Start();
        Assert.True(service.IsRunning);

        service.Stop();
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void SyncScheduleService_UpdateSettings_TriggersEventAndPersists()
    {
        using var tempScope = new TempSettingsFileScope();
        using var service = new SyncScheduleService();

        bool eventFired = false;
        service.SettingsChanged += (s, e) => eventFired = true;

        var newSettings = new ScheduleSettings
        {
            IsEnabled = true,
            ScheduledTime = new TimeSpan(20, 0, 0),
            ScheduledDays = new List<DayOfWeek> { DayOfWeek.Friday }
        };

        service.UpdateSettings(newSettings);

        Assert.True(eventFired);
        Assert.True(service.IsRunning);
        Assert.Equal(new TimeSpan(20, 0, 0), service.Settings.ScheduledTime);
    }
}
