namespace McpServerDotNet;

public interface ITimeService
{
    DateTime GetCurrentTime();
    string GetTimezone();
    string GetFormattedTime(string format);
}

public sealed class TimeService : ITimeService
{
    public DateTime GetCurrentTime() => DateTime.Now;

    public string GetTimezone() => TimeZoneInfo.Local.DisplayName;

    public string GetFormattedTime(string format) => DateTime.Now.ToString(format);
}