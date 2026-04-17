using AgentHappey.Common.Models;
using Microsoft.Extensions.AI;

namespace AgentHappey.Core.ChatRuntime;

public sealed record ChatRuntimeRequest(
    IReadOnlyList<ChatMessage> Messages,
    string? Model,
    IReadOnlyList<string>? Models,
    IReadOnlyList<Agent>? Agents,
    string WorkflowType = "sequential",
    WorkflowMetadata? WorkflowMetadata = null);
