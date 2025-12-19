using Microsoft.Extensions.DependencyInjection;

namespace AIOMarketMaker.Console.Tasks;

public static class TaskServiceExtensions
{
    public static IServiceCollection AddTask<T>(this IServiceCollection services) where T : class, ITask
    {
        services.AddScoped<ITask, T>();
        return services;
    }

    public static IServiceCollection AddTaskRunner(this IServiceCollection services)
    {
        services.AddScoped<TaskRunner>();
        return services;
    }
}
