using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace AI.VolatileTools;

public class VolatileTools(ILogger<VolatileTools> logger) : IVolatileTools, IAgentTools
{
    [Description("Gets the current date and time in UTC.")]
    public string GetCurrentDateTimeUtc()
    {
        logger.LogInformation("Invoking tool: GetCurrentDateTimeUtc");
        return DateTime.UtcNow.ToString("o");
    }

    public List<AITool> GetTools()
    {
        logger.LogInformation("Getting tools for volatile agent");
        return new List<AITool>()
        {
            AIFunctionFactory.Create(GetCurrentDateTimeUtc)
        };
    }
}