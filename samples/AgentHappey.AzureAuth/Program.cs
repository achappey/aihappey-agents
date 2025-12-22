using AgentHappey.Agents.JSON;
using AgentHappey.AzureAuth;
using AgentHappey.Core;
using AgentHappey.Core.MCP;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);
var appConfig = builder.Configuration.Get<Config>();

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
.AddSingleton<IStreamingContentMapper, StreamingContentMapper>()
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
