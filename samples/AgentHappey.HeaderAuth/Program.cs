using AgentHappey.Agents.JSON;
using AgentHappey.Core;
using AgentHappey.Core.MCP;
using AgentHappey.HeaderAuth;

var builder = WebApplication.CreateBuilder(args);
var basePath = Path.Combine(AppContext.BaseDirectory, "Agents");
var appConfig = builder.Configuration.Get<Config>();

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
builder.Services.AddSingleton<IStreamingContentMapper, StreamingContentMapper>();
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
