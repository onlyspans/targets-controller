namespace Onlyspans.TargetsController;

public static partial class Startup
{
    public static IServiceCollection AddGrpcServices(this IServiceCollection services, IHostEnvironment environment)
    {
        services.AddGrpc();

        if (environment.IsDevelopment())
            services.AddGrpcReflection();

        return services;
    }

    public static WebApplication UseGrpcServices(this WebApplication app)
    {
        app.MapGrpcService<Services.TargetsGrpcService>();
        app.MapGrpcService<Services.AgentsGrpcService>();

        if (app.Environment.IsDevelopment())
            app.MapGrpcReflectionService();

        return app;
    }
}
