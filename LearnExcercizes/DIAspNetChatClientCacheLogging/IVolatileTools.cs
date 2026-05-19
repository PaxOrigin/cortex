using Microsoft.Extensions.AI;

namespace AI.VolatileTools;

public interface IVolatileTools : IAgentTools
{
    public string GetCurrentDateTimeUtc();
}