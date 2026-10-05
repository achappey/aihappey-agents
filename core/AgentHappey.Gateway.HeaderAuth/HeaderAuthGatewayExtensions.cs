using AgentHappey.Agents.JSON;
using AgentHappey.Common.Models;
using AgentHappey.Core;
using AgentHappey.Core.ChatRuntime;
using AgentHappey.Core.MCP;
using AgentHappey.Core.Responses;
using AgentHappey.HeaderAuth.Controllers;
using Microsoft.Extensions.Options;

namespace AgentHappey.HeaderAuth;

/// <summary>Portable runtime and HTTP endpoints; hosts select storage and identity separately.</summary>
public static class HeaderAuthGatewayExtensions
{
    public static WebApplicationBuilder AddHeaderAuthGateway(
        this WebApplicationBuilder builder,
        Action<HeaderAuthHostOptions>? configure = null)
    {
        var services = builder.Services;
        var config = builder.Configuration.Get<Config>() ?? new Config();
        services.Configure<Config>(builder.Configuration);
        services.AddOptions<HeaderAuthHostOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.AddCors(options => options.AddPolicy("AllowSpecificOrigin", policy => policy
            .AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()
            .WithExposedHeaders("WWW-Authenticate")
            .WithExposedHeaders("Mcp-Session-Id")));
        services.AddHttpContextAccessor();
        services.AddControllers().AddApplicationPart(typeof(ModelsController).Assembly);
        services.AddSingleton<IResponsesNativeMapper, ResponsesNativeMapper>();
        services.AddSingleton<IStreamingContentMapper, StreamingContentMapper>();
        services.AddSingleton<IChatRuntimeOrchestrator, ChatRuntimeOrchestrator>();
        services.AddSingleton<IModelCatalog, ModelCatalog>();
        services.AddSingleton<IModelSource>(_ => new JsonModelSource(
            Path.Combine(AppContext.BaseDirectory, "Agents"), config.McpConfig?.McpBaseUrl));
        services.AddSingleton<ForegroundResponsePersistence>();
        services.AddHttpClient();
        services.AddMcpServers();
        services.AddSingleton(config.AiConfig!);
        services.AddSingleton(config.McpConfig!);
        return builder;
    }

    public static WebApplication MapHeaderAuthGateway(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<HeaderAuthHostOptions>>().Value;
        app.UseRouting();
        app.UseCors("AllowSpecificOrigin");
        app.MapControllers();
        app.AddMcpMappings(portableOnly: options.PortableMcpOnly);
        app.AddMcpRegistry(app.Services.GetRequiredService<McpConfig>(), portableOnly: options.PortableMcpOnly);
        return app;
    }
}
