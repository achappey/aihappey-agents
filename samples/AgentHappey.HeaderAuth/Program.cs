using AgentHappey.Agents.Blob;
using AgentHappey.Common.Models;
using AgentHappey.AsyncResponses;
using AgentHappey.HeaderAuth;
using AgentHappey.HeaderAuth.AsyncResponses;

var builder = WebApplication.CreateBuilder(args);
builder.AddHeaderAuthGateway();
var appConfig = builder.Configuration.Get<Config>();
builder.Services.AddSingleton<IModelSource>(_ => new BlobModelSource(appConfig?.BlobAgents));
builder.Services.AddAsyncAgentResponses<HeaderAuthAsyncResponsesProcessor>(builder.Configuration);

var app = builder.Build();
app.MapHeaderAuthGateway();
app.Run();
