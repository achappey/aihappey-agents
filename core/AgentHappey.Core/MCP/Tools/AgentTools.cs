using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Mime;
using System.Text.Json;
using AgentHappey.Common.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentHappey.Core.MCP.Tools;

[McpServerToolType]
public class AgentTools
{
    [Description("List available agents.")]
    [McpServerTool(Title = "List available agents", Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<ContentBlock> Agents_List(
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var agents = services.GetRequiredService<ReadOnlyCollection<Agent>>();
        var context = services.GetRequiredService<IHttpContextAccessor>();

        return new EmbeddedResourceBlock()
        {
            Resource = new TextResourceContents()
            {
                Text = JsonSerializer.Serialize(agents, JsonSerializerOptions.Web),
                MimeType = MediaTypeNames.Application.Json,
                Uri = context.HttpContext?.Request.GetDisplayUrl()!
            }
        };
    }
}
