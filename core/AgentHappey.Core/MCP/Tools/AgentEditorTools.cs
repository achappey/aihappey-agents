using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHappey.Common.Extensions;
using AgentHappey.Common.Models;
using AgentHappey.Core.Extensions;
using Microsoft.Graph.Beta;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NJsonSchema;
using NJsonSchema.Generation;

namespace AgentHappey.Core.MCP.Tools;

[McpServerToolType]
public class AgentEditorTools
{
    [Description("Get agent JSON schema.")]
    [McpServerTool(Title = "Get agent JSON schema", Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult> AgentEditor_GetSchema()
    {
        var generator = new JsonSchemaGenerator(new SystemTextJsonSchemaGeneratorSettings
        {
            SchemaType = SchemaType.JsonSchema
        });

        var schema = generator.Generate(typeof(Agent));

        return new CallToolResult()
        {
            StructuredContent = JsonNode.Parse(schema.ToJson())!
        };
    }

    [Description("Create an agent.json in a SharePoint/OneDrive folder link (will fail if file already exists unless overwrite=true).")]
    [McpServerTool(
       Title = "Create agent.json in SharePoint folder",
       Name = "agent_editor_sharepoint_create",
       Idempotent = false,
       ReadOnly = false,
       OpenWorld = true)]
    public static async Task<CallToolResult> AgentEditor_SharePointCreate(
       [Description("SharePoint or OneDrive folder link (sharing link)")] string folderUrl,
        string agentName,
        string agentDescription,
        string agentInstructions,
        string modelId,
        float? modelTemperature,
        string? modelProviderMetadataJson,
        IEnumerable<string>? mcpServerUrls,
        bool? policyReadOnly,
        bool? policyIdempotent,
        bool? policyOpenWorld,
        bool? policyDestructive,
        bool? capabilitySampling,
        bool? capabilityElicitation,
       [Description("File name to create (default: agent.json)")] string? fileName,
       [Description("If true, replaces existing file. If false, Graph may return conflict if file exists.")] bool? overwrite,
       IServiceProvider services,
       RequestContext<CallToolRequestParams> _,
       CancellationToken cancellationToken = default)
    {
        var graph = await services.GetOboGraphClientAsync(
            ["https://graph.microsoft.com/.default"],
            cancellationToken) ?? throw new Exception("Graphclient not found");

        var shareId = folderUrl.EncodeSharingUrl();

        // Resolve folder metadata (need driveId + folderId)
        var folderItem = await graph.Shares[shareId].DriveItem.GetAsync(rc =>
        {
            rc.QueryParameters.Select = ["id", "parentReference", "folder", "webUrl"];
        }, cancellationToken) ?? throw new Exception("Folder not found");

        if (folderItem.Folder is null)
            throw new Exception("Provided URL does not resolve to a folder.");

        var folderId = folderItem.Id ?? throw new Exception("Folder id not found.");
        var driveId = folderItem.ParentReference?.DriveId ?? throw new Exception("Drive id not found.");

        var name = string.IsNullOrWhiteSpace(fileName) ? "agent.json" : fileName.Trim();

        var agent = new Agent
        {
            Name = agentName,
            Description = agentDescription,
            Instructions = agentInstructions,
            Model = new AIModel
            {
                Id = modelId,
                Options = modelTemperature.HasValue ? new AIModelOptions
                {
                    Temperature = modelTemperature
                } : null,
                ProviderMetadata = modelProviderMetadataJson != null
                    ? JsonSerializer.Deserialize<Dictionary<string, object>>(modelProviderMetadataJson)
                    : null
            },
            McpServers = mcpServerUrls?.Select(url => url.ToMcpServer()).ToDictionary(a => a.Url.ToReverseDnsKey(), a => a),
            McpClient = new()
            {
                Policy = new McpPolicy
                {
                    ReadOnly = policyReadOnly,
                    Idempotent = policyIdempotent,
                    OpenWorld = policyOpenWorld,
                    Destructive = policyDestructive
                },
                Capabilities = new ClientCapabilities
                {
                    Sampling = capabilitySampling == true ? new() : null,
                    Elicitation = capabilityElicitation == true ? new() : null
                }
            },
            /* Mcp = new Mcp
             {
                 Servers = mcpServerUrls?.Select(url => url.ToMcpServer()),
                 Policy = new McpPolicy
                 {
                     ReadOnly = policyReadOnly,
                     Idempotent = policyIdempotent,
                     OpenWorld = policyOpenWorld,
                     Destructive = policyDestructive
                 },
                 ClientCapabilities = new ClientCapabilities
                 {
                     Sampling = capabilitySampling == true ? new() : null,
                     Elicitation = capabilityElicitation == true ? new() : null
                 }
             }*/
        };


        var json = JsonSerializer.Serialize(agent, JsonSerializerOptions.Web);
        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var created = await graph.Drives[driveId]
            .Items[folderId]
            .ItemWithPath(name)
            .Content
            .PutAsync(ms, rc =>
            {
                rc.Headers.Add("Content-Type", "application/json; charset=utf-8");
            }, cancellationToken) ?? throw new Exception("Upload failed");

        return new CallToolResult
        {
            StructuredContent = JsonNode.Parse(JsonSerializer.Serialize(created, JsonSerializerOptions.Web))!
        };
    }

    [Description("Edit an existing SharePoint/OneDrive agent.json file by overwriting its content.")]
    [McpServerTool(
        Title = "Edit agent.json in SharePoint",
        Name = "agent_editor_sharepoint_edit",
        Idempotent = false,
        ReadOnly = false,
        OpenWorld = false)]
    public static async Task<CallToolResult> AgentEditor_SharePointEdit(
        [Description("SharePoint or OneDrive link to the agent.json file (sharing link)")] string agentJsonFileUrl,
        string agentName,
        string agentDescription,
        string agentInstructions,
        string modelId,
        float? modelTemperature,
        string? modelProviderMetadataJson,
        IEnumerable<string>? mcpServerUrls,
        bool? policyReadOnly,
        bool? policyIdempotent,
        bool? policyOpenWorld,
        bool? policyDestructive,
        bool? capabilitySampling,
        bool? capabilityElicitation,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken cancellationToken = default)
    {
        var graph = await services.GetOboGraphClientAsync(
            ["https://graph.microsoft.com/.default"],
            cancellationToken) ?? throw new Exception("Graphclient not found");

        var shareId = agentJsonFileUrl.EncodeSharingUrl();

        // Sanity: resolve to DriveItem (and ensure it's a file)
        var item = await graph.Shares[shareId].DriveItem.GetAsync(rc =>
        {
            rc.QueryParameters.Select = ["id", "file", "webUrl", "name"];
        }, cancellationToken) ?? throw new Exception("Agent file not found");

        if (item.File is null)
            throw new Exception("Provided URL does not resolve to a file.");

        var agent = new Agent
        {
            Name = agentName,
            Description = agentDescription,
            Instructions = agentInstructions,
            Model = new AIModel
            {
                Id = modelId,
                Options = modelTemperature.HasValue ? new AIModelOptions
                {
                    Temperature = modelTemperature
                } : null,
                ProviderMetadata = modelProviderMetadataJson != null
                         ? JsonSerializer.Deserialize<Dictionary<string, object>>(modelProviderMetadataJson)
                         : null
            },
            McpServers = mcpServerUrls?
                .Select(url => url.ToMcpServer())
                .ToDictionary(a => a.Url.ToReverseDnsKey(), a => a),
            McpClient = new()
            {
                Policy = new McpPolicy
                {
                    ReadOnly = policyReadOnly,
                    Idempotent = policyIdempotent,
                    OpenWorld = policyOpenWorld,
                    Destructive = policyDestructive
                },
                Capabilities = new ClientCapabilities
                {
                    Sampling = capabilitySampling == true ? new() : null,
                    Elicitation = capabilityElicitation == true ? new() : null
                }
            },
            /*Mcp = new Mcp
            {
                Servers = mcpServerUrls?.Select(url => url.ToMcpServer()),
                Policy = new McpPolicy
                {
                    ReadOnly = policyReadOnly,
                    Idempotent = policyIdempotent,
                    OpenWorld = policyOpenWorld,
                    Destructive = policyDestructive
                },
                ClientCapabilities = new ClientCapabilities
                {
                    Sampling = capabilitySampling == true ? new() : null,
                    Elicitation = capabilityElicitation == true ? new() : null
                }
            }*/
        };

        var json = JsonSerializer.Serialize(agent, JsonSerializerOptions.Web);
        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var updated = await graph.Shares[shareId]
            .DriveItem
            .Content
            .PutAsync(ms, rc =>
            {
                rc.Headers.Add("Content-Type", "application/json; charset=utf-8");
            }, cancellationToken) 
            ?? throw new Exception("Upload failed");

        return new CallToolResult
        {
            StructuredContent = JsonNode.Parse(JsonSerializer.Serialize(updated, JsonSerializerOptions.Web))!
        };
    }
}
