using Onlyspans.TargetsController.Registry;

namespace Onlyspans.TargetsController;

public static partial class Startup
{
    public static IServiceCollection AddConnectionRegistry(this IServiceCollection services)
    {
        services.AddSingleton<ITargetConnectionRegistry, TargetConnectionRegistry>();
        return services;
    }
}
