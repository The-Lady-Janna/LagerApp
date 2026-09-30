using Lager.Infrastructure.Backup;

namespace Lager.Tests.WP25;

/// <summary>
/// Die Zeitplan-Berechnung des Backup-Jobs als reine Funktionen (ohne Uhr, ohne Host): wann ist der nächste Lauf?
/// </summary>
public class BackupScheduleTests
{
    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute, int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    [Theory]
    [InlineData("02:00", 2, 0)]
    [InlineData("2:05", 2, 5)]
    [InlineData("  23:59 ", 23, 59)]
    [InlineData("00:00", 0, 0)]
    public void A_daily_time_in_HH_mm_is_parsed(string text, int hour, int minute)
    {
        Assert.True(BackupSchedule.TryParse(text, out var time));
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("0200")]
    [InlineData("02:00:30")]
    [InlineData("täglich")]
    [InlineData("0 2 * * *")]
    public void An_empty_or_invalid_schedule_means_off(string? text)
    {
        Assert.False(BackupSchedule.TryParse(text, out _));
    }

    [Fact]
    public void The_next_run_is_today_when_the_time_is_still_ahead_else_tomorrow()
    {
        var at = new TimeOnly(2, 0);

        Assert.Equal(Utc(2026, 3, 10, 2, 0), BackupSchedule.NextRun(Utc(2026, 3, 10, 0, 0), at));
        Assert.Equal(Utc(2026, 3, 10, 2, 0), BackupSchedule.NextRun(Utc(2026, 3, 10, 1, 59, 59), at));
        Assert.Equal(Utc(2026, 3, 11, 2, 0), BackupSchedule.NextRun(Utc(2026, 3, 10, 2, 0, 1), at));
        Assert.Equal(Utc(2026, 3, 11, 2, 0), BackupSchedule.NextRun(Utc(2026, 3, 10, 23, 59), at));
    }

    [Fact]
    public void The_next_run_is_strictly_after_now_so_a_run_finishing_on_the_start_time_does_not_start_again()
    {
        var at = new TimeOnly(2, 0);

        // genau zur Startzeit: der nächste Lauf ist morgen, nicht "jetzt" (sonst liefe der Job in einer Schleife)
        Assert.Equal(Utc(2026, 3, 11, 2, 0), BackupSchedule.NextRun(Utc(2026, 3, 10, 2, 0), at));
    }

    [Fact]
    public void The_next_run_rolls_over_month_and_year_and_leap_days()
    {
        var at = new TimeOnly(3, 30);

        Assert.Equal(Utc(2027, 1, 1, 3, 30), BackupSchedule.NextRun(Utc(2026, 12, 31, 12, 0), at));
        Assert.Equal(Utc(2026, 3, 1, 3, 30), BackupSchedule.NextRun(Utc(2026, 2, 28, 12, 0), at));
        Assert.Equal(Utc(2028, 2, 29, 3, 30), BackupSchedule.NextRun(Utc(2028, 2, 28, 12, 0), at));
    }

    [Fact]
    public void The_schedule_is_in_UTC_whatever_offset_the_clock_reports()
    {
        var at = new TimeOnly(2, 0);

        // 23:30 in Berlin-Winterzeit (+01:00) = 22:30 UTC: der nächste Lauf ist um 02:00 UTC des Folgetags
        var berlin = new DateTimeOffset(2026, 1, 10, 23, 30, 0, TimeSpan.FromHours(1));
        Assert.Equal(Utc(2026, 1, 11, 2, 0), BackupSchedule.NextRun(berlin, at));

        // 03:00 in Berlin (+01:00) = 02:00 UTC: genau die Startzeit, also erst morgen
        var sameInstant = new DateTimeOffset(2026, 1, 10, 3, 0, 0, TimeSpan.FromHours(1));
        Assert.Equal(Utc(2026, 1, 11, 2, 0), BackupSchedule.NextRun(sameInstant, at));
    }
}
