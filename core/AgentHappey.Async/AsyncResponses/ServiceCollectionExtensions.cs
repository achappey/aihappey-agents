using AgentHappey.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentHappey.AsyncResponses;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAsyncAgentResponses<TProcessor>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TProcessor : class, IAsyncResponsesProcessor
    {
        var asyncConfig = configuration.GetSection("AsyncAgents").Get<AsyncAgentsConfig>();
        services.Configure<AsyncAgentsConfig>(configuration.GetSection("AsyncAgents"));

        if (!string.IsNullOrWhiteSpace(asyncConfig?.ConnectionString))
        {
            services.AddSingleton<IAsyncResponseStore, AzureAsyncResponseStore>();
            services.AddSingleton<IAsyncResponsesService, AzureAsyncResponsesService>();
            services.AddSingleton<IAsyncResponsesProcessor, TProcessor>();
            services.AddHostedService<AsyncResponsesWorker>();
        }
        else
        {
            services.AddSingleton<IAsyncResponsesService, DisabledAsyncResponsesService>();
        }

        return services;
    }
}
