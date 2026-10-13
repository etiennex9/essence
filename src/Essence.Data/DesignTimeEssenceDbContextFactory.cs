using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Essence.Data;

/// <summary>Used by <c>dotnet ef</c> only. Adding a migration doesn't connect, so the default is a placeholder.</summary>
public sealed class DesignTimeEssenceDbContextFactory : IDesignTimeDbContextFactory<EssenceDbContext>
{
    public EssenceDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ESSENCE_DESIGN_CONNECTION")
            ?? "Host=localhost;Database=essence;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<EssenceDbContext>();
        options.UseEssence(connectionString);
        return new EssenceDbContext(options.Options);
    }
}
