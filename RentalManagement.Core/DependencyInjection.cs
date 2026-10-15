// =====================================================================================================================
// Module: RentalManagement.Core / DependencyInjection.cs
// Purpose: The Core layer's composition root. Registers the clock, the seeder and the business services (property,
//          applicant workflow and review) with their lifetimes, so the Web project only has to call
//          AddRentalManagementCore().
// =====================================================================================================================

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RentalManagement.Core.Data.Seeding;
using RentalManagement.Core.Services.Applications;
using RentalManagement.Core.Services.Properties;

namespace RentalManagement.Core;

/// <summary>
/// Registers the Core layer's services. The Web project calls <see cref="AddRentalManagementCore"/> once from
/// Program.cs, after registering the DbContext and Identity, so Core decides its own registrations and the Web
/// project never news up a Core service.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds every Core service to <paramref name="services"/>: <see cref="TimeProvider.System"/> (singleton, kept if
    /// a test already registered a fake), and the seeder and business services (scoped, one per request, matching the
    /// DbContext's lifetime).
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddRentalManagementCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Business rules ask TimeProvider for "today" (lease terms, active-lease checks) so tests can control time.
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<DatabaseSeeder>();
        services.AddScoped<PropertyService>();
        services.AddScoped<ApplicationService>();
        services.AddScoped<ApplicationReviewService>();
        services.AddScoped<ReviewQueueService>();

        return services;
    }
}
