using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Essence.Data;

public static class EssenceDbServiceCollectionExtensions
{
    /// <summary>Registers <see cref="EssenceDbContext"/> on PostgreSQL with snake_case naming.</summary>
    public static IServiceCollection AddEssenceDbContext(
        this IServiceCollection services,
        string connectionString,
        Action<DbContextOptionsBuilder>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return services.AddDbContext<EssenceDbContext>(options =>
        {
            options.UseEssence(connectionString);
            configure?.Invoke(options);
        });
    }

    public static DbContextOptionsBuilder UseEssence(this DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();
}
