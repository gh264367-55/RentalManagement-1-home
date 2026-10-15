// =====================================================================================================================
// Module: RentalManagement.Core / Data / ApplicationDbContext.cs
// Purpose: The application's only EF Core DbContext. It combines the ASP.NET Core Identity schema with the rental
//          tables in one SQL Server database and one migration history, exposes a DbSet per aggregate, and applies the
//          model-wide conventions (enums stored as strings, money as decimal(18,2)).
// =====================================================================================================================

using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RentalManagement.Core.Domain;

namespace RentalManagement.Core.Data;

/// <summary>
/// The single EF Core context: ASP.NET Identity tables plus the rental tables, in one database with one migration
/// history. Entity mappings live in <c>Data/Configurations</c>, one <see cref="IEntityTypeConfiguration{T}"/> per entity.
/// Migrations live in <c>Data/Migrations</c> in this assembly.
/// </summary>
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    /// <summary>The Unit Type lookup.</summary>
    public DbSet<UnitType> UnitTypes => Set<UnitType>();

    /// <summary>Properties (buildings/complexes).</summary>
    public DbSet<Property> Properties => Set<Property>();

    /// <summary>Rentable units.</summary>
    public DbSet<Unit> Units => Set<Unit>();

    /// <summary>Rental applications.</summary>
    public DbSet<RentalApplication> RentalApplications => Set<RentalApplication>();

    /// <summary>Prior residences listed on applications.</summary>
    public DbSet<Residence> Residences => Set<Residence>();

    /// <summary>The audit trail of application status changes.</summary>
    public DbSet<ApplicationStatusHistory> ApplicationStatusHistory => Set<ApplicationStatusHistory>();

    /// <summary>Leases issued on approval.</summary>
    public DbSet<Lease> Leases => Set<Lease>();

    /// <summary>
    /// Builds the model: Identity's own mappings first, then every <see cref="IEntityTypeConfiguration{T}"/> found in
    /// this assembly (<c>Data/Configurations</c>).
    /// </summary>
    /// <param name="builder">The model builder supplied by EF Core.</param>
    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Identity's mappings first, so our configurations can refine them (e.g. ApplicationUser name lengths).
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }

    /// <summary>
    /// Model-wide conventions applied before the per-entity configurations: status and outcome enums stored as
    /// strings (max 20 characters), and every decimal stored as decimal(18,2).
    /// </summary>
    /// <param name="configurationBuilder">The convention builder supplied by EF Core.</param>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums as strings: readable in SQL and safe if the enum is ever reordered.
        configurationBuilder.Properties<ApplicationStatus>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<ReviewOutcome>().HaveConversion<string>().HaveMaxLength(20);

        // Money everywhere.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    }
}
