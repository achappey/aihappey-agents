
using System.Text.Json.Nodes;
using AIHappey.Common.Model;

namespace AgentHappey.Common.Models;

public class AgentRequest
{
    public IEnumerable<Agent> Agents { get; set; } = null!;

    public string WorkflowType { get; set; } = "sequential";

    public string? WorkflowFile { get; set; }

    public string Id { get; set; } = null!;

    public List<UIMessage> Messages { get; set; } = null!;

    public WorkflowMetadata? WorkflowMetadata { get; set; }
}

public class WorkflowMetadata
{
    public Groupchat? Groupchat { get; set; }

    public Handoff? Handoff { get; set; }

}

public class Groupchat
{
    public int MaximumIterationCount { get; set; } = 5;

}

public class Handoff
{
    public JsonArray? Handoffs { get; set; }

}