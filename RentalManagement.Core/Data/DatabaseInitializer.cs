// =====================================================================================================================
// Module: RentalManagement.Core / Data / DatabaseInitializer.cs
// Purpose: Startup routine called from Program.cs before the web app accepts requests. It logs the target server and
//          database, creates the database if needed, applies pending EF Core migrations, and runs the idempotent seeder
//          when seeding is enabled.
// =====================================================================================================================

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalManagement.Core.Data.Seeding;

namespace RentalManagement.Core.Data;

/// <summary>Brings the database up to date on start. Program.cs calls this before the app starts listening.</summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// Creates the database if it doesn't exist, applies pending migrations, then runs the idempotent seeder.
    /// </summary>
    /// <param name="services">The application's root service provider; a scope is created for the work.</param>
    /// <param name="cancellationToken">Cancels migration and seeding.</param>
    /// <returns>A task that completes when the database is ready.</returns>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseInitializer).FullName!);
        var db = provider.GetRequiredService<ApplicationDbContext>();

        // Log where we are connecting (server and database only, never credentials) so a wrong target is obvious.
        var target = new SqlConnectionStringBuilder(db.Database.GetConnectionString());
        logger.LogInformation(
            "Applying migrations to database {Database} on {Server}", target.InitialCatalog, target.DataSource);
        await db.Database.MigrateAsync(cancellationToken);

        if (provider.GetRequiredService<IOptions<SeedOptions>>().Value.Enabled)
        {
            logger.LogInformation("Seeding data");
            await provider.GetRequiredService<DatabaseSeeder>().SeedAsync(cancellationToken);
        }

        logger.LogInformation("Database ready");
    }
}
