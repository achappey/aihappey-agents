using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentHappey.Core.Diagnostics;

public static class DebugServiceExtensions
{
    public static IServiceCollection AddProviderDebugEvents(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddScoped<RequestDebugEvents>();
        services.TryAddScoped<IProviderDebugEmitter>(sp => sp.GetRequiredService<RequestDebugEvents>());
        return services;
    }
}
