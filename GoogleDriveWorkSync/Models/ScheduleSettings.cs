using System;
using System.Collections.Generic;

namespace GoogleDriveWorkSync.Models;

public class ScheduleSettings
{
    public bool IsEnabled { get; set; } = false;

    /// <summary>
    /// Exact daily time for automated background synchronization.
    /// </summary>
    public TimeSpan ScheduledTime { get; set; } = new TimeSpan(18, 0, 0); // Default 18:00

    /// <summary>
    /// Days of the week on which background sync should fire.
    /// </summary>
    public List<DayOfWeek> ScheduledDays { get; set; } = new()
    {
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday
    };

    /// <summary>
    /// Whether to auto-sync both Work Files and Claude AI Context, or just Work Files.
    /// </summary>
    public bool SyncClaudeContextAlso { get; set; } = true;

    /// <summary>
    /// Date of the last automated background run, ensuring it only fires once per day.
    /// </summary>
    public DateTime? LastAutoSyncDate { get; set; }

    public bool ShouldRunAt(DateTime currentTime)
    {
        if (!IsEnabled) return false;
        if (!ScheduledDays.Contains(currentTime.DayOfWeek)) return false;

        // If already ran today, skip
        if (LastAutoSyncDate.HasValue && LastAutoSyncDate.Value.Date == currentTime.Date)
            return false;

        // Run if current time is past or at the scheduled time
        return currentTime.TimeOfDay >= ScheduledTime;
    }
}
