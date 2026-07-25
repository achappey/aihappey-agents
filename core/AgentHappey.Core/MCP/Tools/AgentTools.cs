using System.ComponentModel;
using System.Text.Json;
using AgentHappey.Common.Models;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentHappey.Core.MCP.Tools;

[McpServerToolType]
public class AgentTools
{
    [Description("List available Agent Framework models. Use Agents_Get to retrieve an agent's complete definition.")]
    [McpServerTool(
        Name = "agents_list",
        Title = "List available agents",
        Idempotent = true,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult> Agents_List(
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var modelCatalog = services.GetRequiredService<IModelCatalog>();
        var models = await modelCatalog.ListAsync(ct);

        return new()
        {
            StructuredContent = JsonSerializer.SerializeToElement(
                new ModelListResponse
                {
                    Object = models.Object,
                    Data = models.Data
                        .Select(model => new Model
                        {
                            Id = model.Id,
                            Object = model.Object,
                            Created = model.Created,
                            OwnedBy = model.OwnedBy,
                            Name = model.Name,
                            Description = model.Description,
                            Type = model.Type
                        })
                        .ToList()
                        .AsReadOnly()
                },
                JsonSerializerOptions.Web)
        };
    }

    [Description("Get the complete Agent Framework agent definition for a model exposed by this runtime.")]
    [McpServerTool(
        Name = "agents_get",
        Title = "Get an agent definition",
        Idempotent = true,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult> Agents_Get(
        [Description("The agent model ID returned by agents_list.")] string agentName,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var modelCatalog = services.GetRequiredService<IModelCatalog>();
        var agent = await modelCatalog.ResolveAgentAsync(agentName, ct);

        if (agent == null)
        {
            return new()
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"Agent '{agentName}' was not found." }]
            };
        }

        return new()
        {
            StructuredContent = JsonSerializer.SerializeToElement(agent, JsonSerializerOptions.Web)
        };
    }
}
