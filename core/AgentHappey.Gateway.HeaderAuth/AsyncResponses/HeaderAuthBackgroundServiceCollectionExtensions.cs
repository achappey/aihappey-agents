using AgentHappey.AsyncResponses;
using AgentHappey.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentHappey.HeaderAuth.AsyncResponses;

public static class HeaderAuthBackgroundServiceCollectionExtensions
{
    /// <summary>Hosted HeaderAuth only. Does not register a durable execution queue or payload store.</summary>
    public static IServiceCollection AddHeaderAuthBackgroundResponses(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AsyncAgentsConfig>(configuration.GetSection("AsyncAgents"));
        services.AddOptions<HeaderAuthBackgroundOptions>().Bind(configuration.GetSection("HeaderAuthBackground"))
            .Validate(options => options.Capacity > 0 && options.ExecutionLifetime > TimeSpan.Zero
                && options.ExecutionLifetime <= TimeSpan.FromDays(1) && options.SweepInterval > TimeSpan.Zero,
                "HeaderAuth background capacity, execution lifetime and sweep interval must be positive; lifetime cannot exceed one day.")
            .ValidateOnStart();
        if (string.IsNullOrWhiteSpace(configuration.GetSection("AsyncAgents").Get<AsyncAgentsConfig>()?.ConnectionString))
        {
            services.AddSingleton<IAsyncResponsesService, DisabledAsyncResponsesService>();
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);
        // Retain the existing foreground persistence dependency without changing its policy.
        services.AddSingleton<IAsyncResponseStore, AzureAsyncResponseStore>();
        services.AddSingleton<IHeaderAuthBackgroundResponseStore, HeaderAuthBackgroundResponseStore>();
        services.AddSingleton<IAsyncResponsesProcessor, HeaderAuthAsyncResponsesProcessor>();
        services.AddSingleton<HeaderAuthBackgroundResponsesService>();
        services.AddSingleton<IAsyncResponsesService>(provider => provider.GetRequiredService<HeaderAuthBackgroundResponsesService>());
        services.AddHostedService(provider => provider.GetRequiredService<HeaderAuthBackgroundResponsesService>());
        return services;
    }
}
