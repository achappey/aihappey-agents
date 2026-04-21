using AgentHappey.Agents.JSON;
using AgentHappey.Agents.Blob;
using AgentHappey.AzureAuth;
using AgentHappey.Common.Models;
using AgentHappey.Core;
using AgentHappey.Core.ChatRuntime;
using AgentHappey.Core.MCP;
using AgentHappey.Core.Responses;
using AIHappey.Abstractions.Http;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);
var appConfig = builder.Configuration.Get<Config>();

if (builder.Environment.IsDevelopment())
    ProviderBackendCapture.ConfigureDevelopmentDefaults(builder.Environment.ContentRootPath);
else
    ProviderBackendCapture.Disable();

builder.Services.Configure<Config>(builder.Configuration);

var basePath = Path.Combine(AppContext.BaseDirectory, "Agents");

// Add authentication/authorization
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"))
    .EnableTokenAcquisitionToCallDownstreamApi() // enables OBO
    .AddInMemoryTokenCaches();

builder.Services
.AddAuthorization()
.AddCors(options =>
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
})
.AddHttpContextAccessor()
.AddSingleton<IResponsesNativeMapper, ResponsesNativeMapper>()
.AddSingleton<IStreamingContentMapper, StreamingContentMapper>()
.AddSingleton<IChatRuntimeOrchestrator, ChatRuntimeOrchestrator>()
.AddSingleton<IModelCatalog, ModelCatalog>()
.AddSingleton<IModelSource>(_ => new JsonModelSource(basePath, appConfig?.McpConfig?.McpBaseUrl))
.AddSingleton<IModelSource>(_ => new BlobModelSource(appConfig?.BlobAgents))
.AddHttpClient()
.AddMcpServers();

builder.Services.AddControllers();

var staticAgents = basePath.GetAgents(appConfig?.McpConfig?.McpBaseUrl!);
builder.Services.AddSingleton(staticAgents.ToList().AsReadOnly());
builder.Services.AddSingleton(appConfig?.AiConfig!);
builder.Services.AddSingleton(appConfig?.AzureAd!);
builder.Services.AddSingleton(appConfig?.McpConfig!);

var app = builder.Build();

app.UseRouting();
app.UseCors("AllowSpecificOrigin");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.AddMcpMappings(true);
app.AddMcpRegistry(appConfig?.McpConfig!, true);

app.Run();
