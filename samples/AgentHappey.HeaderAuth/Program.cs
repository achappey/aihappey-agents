using AgentHappey.Agents.Blob;
using AgentHappey.Common.Models;
using AgentHappey.HeaderAuth;
using AgentHappey.HeaderAuth.AsyncResponses;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(230);
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(230);
    o.Limits.MaxRequestBodySize = null;
});

builder.AddHeaderAuthGateway();
var appConfig = builder.Configuration.Get<Config>();
builder.Services.AddSingleton<IModelSource>(_ => new BlobModelSource(appConfig?.BlobAgents));
builder.Services.AddHeaderAuthBackgroundResponses(builder.Configuration);

var app = builder.Build();
app.MapHeaderAuthGateway();
app.Run();
