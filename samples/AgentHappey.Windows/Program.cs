using AgentHappey.AsyncResponses;
using AgentHappey.HeaderAuth;
using AgentHappey.HeaderAuth.AsyncResponses;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://localhost:5001");

builder.AddHeaderAuthGateway(options =>
{
    options.PersistForegroundResponses = true;
    options.ListStoredResponses = true;
    options.PortableMcpOnly = true;
});
builder.Services.AddLocalAgentResponses<HeaderAuthAsyncResponsesProcessor>(builder.Configuration);

var app = builder.Build();
app.MapHeaderAuthGateway();
app.Run();
