using AgentHappey.Agents.JSON;
using AgentHappey.Agents.Blob;
using AgentHappey.Common.Models;
using AgentHappey.Core;
using AgentHappey.Core.ChatRuntime;
using AgentHappey.Core.MCP;
using AgentHappey.Core.Responses;
using AIHappey.Abstractions.Http;
using AgentHappey.HeaderAuth;

var builder = WebApplication.CreateBuilder(args);
var basePath = Path.Combine(AppContext.BaseDirectory, "Agents");
var appConfig = builder.Configuration.Get<Config>();

if (builder.Environment.IsDevelopment())
    ProviderBackendCapture.ConfigureDevelopmentDefaults(builder.Environment.ContentRootPath);
else
    ProviderBackendCapture.Disable();

builder.Services.Configure<Config>(builder.Configuration);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowSpecificOrigin", policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("WWW-Authenticate")
            .WithExposedHeaders("Mcp-Session-Id");
    });
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddControllers();
builder.Services.AddSingleton<IResponsesNativeMapper, ResponsesNativeMapper>();
builder.Services.AddSingleton<IStreamingContentMapper, StreamingContentMapper>();
builder.Services.AddSingleton<IChatRuntimeOrchestrator, ChatRuntimeOrchestrator>();
builder.Services.AddSingleton<IModelCatalog, ModelCatalog>();
builder.Services.AddSingleton<IModelSource>(_ => new JsonModelSource(basePath, appConfig?.McpConfig?.McpBaseUrl));
builder.Services.AddSingleton<IModelSource>(_ => new BlobModelSource(appConfig?.BlobAgents));
builder.Services.AddHttpClient();
builder.Services.AddMcpServers();

var staticAgents = basePath.GetAgents(appConfig?.McpConfig?.McpBaseUrl!);
builder.Services.AddSingleton(staticAgents.ToList().AsReadOnly());
builder.Services.AddSingleton(appConfig?.AiConfig!);

var app = builder.Build();

app.UseRouting();
app.UseCors("AllowSpecificOrigin");
app.MapControllers();
app.AddMcpMappings();
app.AddMcpRegistry(appConfig?.McpConfig!);

app.Run();
