using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using AgentHappey.Common.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentHappey.Core.MCP;

public static class ServiceExtensions
{
   public static void AddMcpMappings(this WebApplication app, bool withAuthentication = false)
   {
      foreach (var server in ModelContextServers.Servers)
      {
         var endpoint = app.MapMcp($"/{server.Key}");

         if (withAuthentication)
            endpoint.RequireAuthorization();
      }
   }


   public static string? GetReversedHostFromPath(HttpRequest ctx)
   {
      // extract the first segment
      var host = ctx.Host.Value?
          .Trim('/')
          .Split('/', StringSplitOptions.RemoveEmptyEntries)
          .FirstOrDefault();

      if (string.IsNullOrWhiteSpace(host))
         return null;

      // reverse labels: a.b.c → c.b.a
      var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
      Array.Reverse(parts);
      return string.Join('.', parts);
   }

   public static IServiceCollection AddMcpServers(this IServiceCollection services)
   {
      services
          .AddMcpServer()
          .WithHttpTransport(http =>
          {
             http.ConfigureSessionOptions = async (ctx, opts, cancellationToken) =>
             {
                var server = ctx.Request.Path.Value?
                 .Trim('/')
                 .Split('/', StringSplitOptions.RemoveEmptyEntries)
                 .FirstOrDefault();

                var serverName = ModelContextServers.Servers.FirstOrDefault(a => a.Key.Equals(server, StringComparison.InvariantCultureIgnoreCase)).Key;

                if (server != null && serverName != null)
                {
                   opts.ServerInfo = new Implementation()
                   {
                      Version = "1.0.0",
                      Name = GetReversedHostFromPath(ctx.Request) + "/" + serverName,
                      Title = ModelContextServers.Titles[serverName]
                   };

                   static List<McpServerTool> BuildTools(IServiceProvider sp, Type[] types)
                   {
                      var list = new List<McpServerTool>();

                      foreach (var t in types)
                      {
                         var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

                         foreach (var mi in methods)
                         {
                            if (mi.GetCustomAttribute<McpServerToolAttribute>() is null) continue;

                            McpServerTool tool = mi.IsStatic
                                ? McpServerTool.Create(mi, target: null, new McpServerToolCreateOptions { Services = sp })
                                : McpServerTool.Create(mi,
                                    r => ActivatorUtilities.CreateInstance(r.Services!, t),
                                    new McpServerToolCreateOptions { Services = sp });

                            list.Add(tool);
                         }
                      }
                      return list;
                   }

                   if (ModelContextServers.Resources.TryGetValue(server, out ListResourcesResult? resourceValue))
                   {
                      opts.Handlers.ListResourcesHandler = async (context, _ct) => await Task.FromResult(resourceValue);
                      opts.Handlers.ReadResourceHandler = async (context, _ct) => await Task.FromResult(new ReadResourceResult()
                      {
                         Contents = [..context.Services?.GetRequiredService<ReadOnlyCollection<Agent>>().Select(a => new TextResourceContents()
                         {
                            Text = JsonSerializer.Serialize(a, JsonSerializerOptions.Web),
                            Uri = $"agents://list/{a.Name}",
                            MimeType =  "application/vnd.agent+json",
                         }) ?? []]
                      });
                   }

                   if (ModelContextServers.ToolTypes.TryGetValue(server, out Type[]? value))
                   {
                      // 3) Build per-request views
                      var tools = BuildTools(ctx.RequestServices, value);

                      opts.Handlers.ListToolsHandler = async (context, _ct) =>
                      {
                         var clientFact = context.Services?.GetRequiredService<IHttpClientFactory>()
                           ?? throw new Exception("Something went wrong");

                         var visible = tools
                              .Select(tl => new Tool
                              {
                                 Name = tl.ProtocolTool.Name,
                                 Title = tl.ProtocolTool.Title,
                                 Description = tl.ProtocolTool.Description,
                                 InputSchema = tl.ProtocolTool.InputSchema,
                                 OutputSchema = tl.ProtocolTool.OutputSchema,
                                 Annotations = tl.ProtocolTool.Annotations,
                                 Meta = tl.ProtocolTool.Meta
                              })
                              .ToArray();

                         return await Task.FromResult(new ListToolsResult { Tools = visible });
                      };

                      opts.Handlers.CallToolHandler = async (req, _ct) =>
                      {
                         var name = req.Params?.Name ?? "";
                         var t = tools.FirstOrDefault(x => x.ProtocolTool.Name == name)
                          ?? throw new McpException($"Tool '{name}' not available in '{server}'.");

                         return await t.InvokeAsync(req, _ct);
                      };
                   }
                }

                await Task.CompletedTask;
             };
          });

      return services;
   }

   private static string ToReverseDns(this string host)
   {
      var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
      Array.Reverse(parts);
      return string.Join(".", parts);
   }

   public static void AddMcpRegistry(this WebApplication app, McpConfig mcpConfig, bool withAuthentication = false)
   {
      app.MapGet("/v0.1/servers", (HttpContext context) =>
      {
         var host = context.Request.Host.ToString();
         var scheme = context.Request.Scheme;
         var rev = host.ToReverseDns();

         var icons = new List<Icon>();

         if (!string.IsNullOrEmpty(mcpConfig.DarkIcon))
         {
            icons.Add(new() { Source = mcpConfig.DarkIcon, Theme = "dark" });
         }

         if (!string.IsNullOrEmpty(mcpConfig.LightIcon))
         {
            icons.Add(new() { Source = mcpConfig.LightIcon, Theme = "light" });
         }

         object ServerEntry(string resource) => new
         {
            server = new
            {
               schema = "https://static.modelcontextprotocol.io/schemas/2025-10-17/server.schema.json",
               name = $"{rev}/{resource}",
               title = ModelContextServers.Titles[resource],
               icons,
               description = ModelContextServers.Descriptions.TryGetValue(resource, out var desc)
                        ? desc.Replace("{host}", host)
                        : $"Statistics and usage data for {resource} on {host}.",
               remotes = new[]
               {
                  new
                  {
                     type = "streamable-http",
                     url = $"{scheme}://{host}/{resource.ToLowerInvariant()}"
                  }
               }
            }
         };

         List<object> servers = [];

         foreach (var server in ModelContextServers.Servers)
         {
            if (!server.Value || withAuthentication)
               servers.Add(ServerEntry(server.Key));
         }

         return Results.Json(new { servers });
      })
      .AllowAnonymous();
   }

}
