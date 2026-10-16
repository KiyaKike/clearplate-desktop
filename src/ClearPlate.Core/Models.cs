using System.Globalization;

namespace ClearPlate.Core;

public sealed record Session(string Token, string AccountId, string Nickname);
public sealed record UserProfile(string AccountId, string Nickname, int Successes, int Remaining,
    string IssuedToken, DateTimeOffset? LastCheckIn)
{
    public string WechatLocation { get; init; } = "";
}
public sealed class AppState
{
    public bool ScheduleEnabled { get; set; }
    public bool NeedsLogin { get; set; }
    public string GroupId { get; set; } = "";
    public string Location { get; set; } = "";
    public string LocationSource { get; set; } = "";
    public HashSet<string> Attempted { get; set; } = [];
    public HashSet<string> Confirmed { get; set; } = [];
    public Dictionary<string, SlotRecord> Records { get; set; } = [];
}
public sealed record SlotRecord(string StartedAt, string UpdatedAt, string Stage, string Outcome, string Message)
{
    // Null preserves compatibility with schema-1 records written by old versions.
    public bool? UploadStarted { get; init; }
    public bool? SubmissionStarted { get; init; }
    public string RetryPolicy { get; init; } = "";
}
public enum ConnectionResult { Connected, NeedsCapture, Failed }
public static class PlateLocation
{
    public static string Normalize(string value)
    {
        if (value.Length > 128) return "";
        var parts = value.Split(',');
        if (parts.Length != 2 || !double.TryParse(parts[0], CultureInfo.InvariantCulture, out var latitude)
            || !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var longitude)
            || !double.IsFinite(latitude) || !double.IsFinite(longitude) || Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180)
            return "";
        return latitude.ToString("R", CultureInfo.InvariantCulture) + "," + longitude.ToString("R", CultureInfo.InvariantCulture);
    }
}
public static class PreUploadRecovery
{
    public static bool CanRelease(SlotRecord record) => record.UploadStarted is not true && record.SubmissionStarted is not true
        && record.Stage is ("准备照片" or "查询账号")
        && record.Outcome is ("未成功" or "未提交" or "等待网络" or "等待间隔" or "等待条件" or "等待本机会话");
}
public sealed class LoginRequiredException() : Exception("登录已失效，请在微信正常登录后点击“连接微信”。");
public sealed class BusinessException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
public static class Schedule
{
    public static readonly string[] Slots = ["08:00", "12:00", "18:00"];
    public static DateTimeOffset Beijing(DateTimeOffset date) => date.ToOffset(TimeSpan.FromHours(8));
    public static string Day(DateTimeOffset date) => Beijing(date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Timestamp(DateTimeOffset date) => Beijing(date).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public static string Key(string slot, DateTimeOffset date) => Day(date) + "@" + slot;
    public static string? Due(DateTimeOffset date)
    {
        var now = Beijing(date).TimeOfDay;
        return Slots.FirstOrDefault(slot => now >= TimeSpan.Parse(slot, CultureInfo.InvariantCulture)
            && now < TimeSpan.Parse(slot, CultureInfo.InvariantCulture).Add(TimeSpan.FromMinutes(5)));
    }
    public static string Suggested(DateTimeOffset date) => Due(date)
        ?? Slots.FirstOrDefault(s => string.CompareOrdinal(s, Beijing(date).ToString("HH:mm", CultureInfo.InvariantCulture)) >= 0)
        ?? Slots[^1];
}
