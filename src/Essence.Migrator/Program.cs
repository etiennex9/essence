using Essence.Data;
using Essence.Migrator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// One-shot: apply EF migrations as the admin user, then create/grant the application roles. Exit code 0 on success.
var builder = Host.CreateApplicationBuilder(args);
var adminConnectionString = builder.Configuration.GetConnectionString("Admin");
var writerPassword = builder.Configuration["Roles:WriterPassword"];
var readerPassword = builder.Configuration["Roles:ReaderPassword"];
if (string.IsNullOrWhiteSpace(adminConnectionString) || string.IsNullOrWhiteSpace(writerPassword) || string.IsNullOrWhiteSpace(readerPassword))
{
    Console.Error.WriteLine("ConnectionStrings:Admin, Roles:WriterPassword and Roles:ReaderPassword are required.");
    return 2;
}

builder.Services.AddEssenceDbContext(adminConnectionString);

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Essence.Migrator");
var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

try
{
    await using (var scope = host.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<EssenceDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync(stopping)).ToList();
        logger.LogInformation("Applying {Count} pending migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
        await db.Database.MigrateAsync(stopping);
    }

    await RoleProvisioner.ProvisionAsync(adminConnectionString, writerPassword, readerPassword, logger, stopping);
    logger.LogInformation("Database is up to date.");
    return 0;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Migration failed.");
    return 1;
}
