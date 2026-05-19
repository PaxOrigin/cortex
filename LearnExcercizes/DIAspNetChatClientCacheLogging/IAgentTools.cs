using Microsoft.Extensions.AI;

public interface IAgentTools
{
    public List<AITool> GetTools();
}