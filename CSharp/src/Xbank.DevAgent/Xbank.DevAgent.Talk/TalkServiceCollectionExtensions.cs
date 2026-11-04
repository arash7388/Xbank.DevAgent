using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Xbank.DevAgent.Talk;

public static class TalkServiceCollectionExtensions
{
    public static IServiceCollection AddTalkNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TalkOptions>().Bind(configuration.GetSection(TalkOptions.SectionName))
            .Validate(options => options.IsValid(), "Enabled Talk notifications require a thread ID, token, queue connection settings, service URLs and a timeout between 1 and 120 seconds.")
            .ValidateOnStart();
        services.AddSingleton<ITalkMessageClient, PodTalkMessageClient>();
        services.AddSingleton<TalkNotificationService>();
        services.AddSingleton<ITalkNotifications>(provider => provider.GetRequiredService<TalkNotificationService>());
        services.AddHostedService(provider => provider.GetRequiredService<TalkNotificationService>());
        return services;
    }
}
