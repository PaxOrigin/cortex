using System.ComponentModel;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class TimeTools
{
    [McpServerTool(ReadOnly = true, Idempotent = false)
    , Description("Gets the current date and time in the format 'yyyy-MM-dd HH:mm:ss'.")]
    public static string GetCurrentTime(
    [Description("The timezone identifier, e.g. 'UTC', 'Romance Standard Time', 'Pacific Standard Time'")]
    string? timeZoneId = null)
    {
        TimeZoneInfo timeInfo;
        try
        {
            timeInfo = TimeZoneInfo.FindSystemTimeZoneById(
                timeZoneId ?? TimeZoneInfo.Local.Id);
        }
        catch (TimeZoneNotFoundException)
        {
            return $"Unknown timezone '{timeZoneId}'. Use a valid Windows or IANA timezone ID.";
        }

        return TimeZoneInfo.ConvertTime(DateTime.UtcNow, timeInfo)
            .ToString("yyyy-MM-dd HH:mm:ss");
    }
}