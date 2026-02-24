namespace Onlyspans.TargetsController;

public static partial class Startup
{
    public static IServiceCollection AddHealthz(this IServiceCollection services)
    {
        services.AddHealthChecks();
        return services;
    }

    public static WebApplication UseHealthz(this WebApplication app)
    {
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/health/ready");
        return app;
    }
}
