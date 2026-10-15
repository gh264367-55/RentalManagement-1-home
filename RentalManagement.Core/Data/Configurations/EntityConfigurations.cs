// =====================================================================================================================
// Module: RentalManagement.Core / Data / Configurations / EntityConfigurations.cs
// Purpose: Fluent API mappings for every rental entity, one IEntityTypeConfiguration<T> per entity. They are picked
//          up by ApplicationDbContext.OnModelCreating through ApplyConfigurationsFromAssembly. The mappings set column
//          lengths, owned address columns, concurrency tokens, check constraints, indexes for the list filters and
//          the active-lease lookup, and the delete behaviour of every relationship.
// =====================================================================================================================

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalManagement.Core.Domain;

namespace RentalManagement.Core.Data.Configurations;

// Delete behaviour: only Property → Units and Application → Residences/StatusHistory cascade. Everything else is
// Restrict. That protects history (an application or lease never disappears because a unit or user was removed) and
// avoids SQL Server's "multiple cascade paths" error.

/// <summary>Adds the name columns to Identity's user table.</summary>
internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    /// <summary>Maps <see cref="ApplicationUser"/>: required first and last name, 100 characters each.</summary>
    /// <param name="builder">The entity builder supplied by EF Core.</param>
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(u => u.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(u => u.LastName).HasMaxLength(100).IsRequired();
    }
}

/// <summary>Maps the Unit Type lookup.</summary>
internal sealed class UnitTypeConfiguration : IEntityTypeConfiguration<UnitType>
{
    /// <summary>Maps <see cref="UnitType"/>: a required name that is unique across the lookup.</summary>
    /// <param name="builder">The entity builder supplied by EF Core.</param>
    public void Configure(EntityTypeBuilder<UnitType> builder)
    {
        builder.Property(t => t.Name).HasMaxLength(50).IsRequired();
        builder.HasIndex(t => t.Name).IsUnique();
    }
}

/// <summary>Maps properties and their relationship to units.</summary>
internal sealed class PropertyConfiguration : IEntityTypeConfiguration<Property>
{
    /// <summary>
    /// Maps <see cref="Property"/>: name, the owned address stored as plain columns (Street, City, …), the
    /// concurrency stamp, and the cascading Property → Units relationship.
    /// </summary>
    /// <param name="builder">The entity builder supplied by EF Core.</param>
    public void Configure(EntityTypeBuilder<Property> builder)
    {
        builder.Property(p => p.Name).HasMaxLength(150).IsRequired();
        builder.OwnsOne(p => p.Address, a => AddressMapping.Map(a, string.Empty));
        builder.Navigation(p => p.Address).IsRequired();
        builder.Property(p => p.ConcurrencyStamp).IsConcurrencyToken();

        builder.HasMany(p => p.Units)
            .WithOne(u => u.Property)
            .HasForeignKey(u => u.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps units, their check constraints and their unit type.</summary>
internal sealed class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    /// <summary>
    /// Maps <see cref="Unit"/>: database checks for non-negative bedrooms and positive rent, a unit number that is
    /// unique within its property, the concurrency stamp, and a restricted link to the unit type (a type in use can't
    /// be deleted).
    /// </summary>
    /// <param name="builder">The entity builder supplied by EF Core.</param>
    public void Configure(EntityTypeBuilder<Unit> builder)
    {
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Units_Bedrooms", "[Bedrooms] >= 0");
            t.HasCheckConstraint("CK_Units_MonthlyRent", "[MonthlyRent] > 0");
        });

        builder.Property(u => u.UnitNumber).HasMaxLength(20).IsRequired();
        builder.HasIndex(u => new { u.PropertyId, u.UnitNumber }).IsUnique();
        builder.Property(u => u.ConcurrencyStamp).IsConcurrencyToken();

        builder.HasOne(u => u.UnitType)
            .WithMany()
            .HasForeignKey(u => u.UnitTypeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Maps rental applications, their sections and their relationships.</summary>
internal sealed class RentalApplicationConfiguration : IEntityTypeConfiguration<RentalApplication>
{
    /// <summary>
    /// Maps <see cref="RentalApplication"/>: Section 1 column lengths, the optional current address (columns
    /// prefixed "Current"), the concurrency stamp, the indexes used by the application list filters, restricted links
    /// to the unit and applicant, and cascading residences and status history.
    /// </summary>
    /// <param name="builder">The entity builder supplied by EF Core.</param>
    public void Configure(EntityTypeBuilder<RentalApplication> builder)
    {
        builder.Property(a => a.FirstName).HasMaxLength(100);
        builder.Property(a => a.LastName).HasMaxLength(100);
        builder.Property(a => a.Phone).HasMaxLength(30);
        builder.Property(a => a.Email).HasMaxLength(256);
        builder.OwnsOne(a => a.CurrentAddress, a => AddressMapping.Map(a, "Current"));
        builder.Property(a => a.ConcurrencyStamp).IsConcurrencyToken();

        // The application list filters by status, by applicant + status, and by unit/property.
        builder.HasIndex(a => a.Status);
        builder.HasIndex(a => new { a.ApplicantId, a.Status });

        builder.HasOne(a => a.Unit)
            .WithMany(u => u.Applications)
            .HasForeignKey(a => a.UnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Applicant)
            .WithMany()
            .HasForeignKey(a => a.ApplicantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Review claim: the claiming manager is a real Identity user (restricted, so a claim can't be orphaned), and the
        // database itself refuses a claim without Under Review or Under Review without a claim. The review queue looks
        // up "Under Review, claimed by me" and "Submitted", so the index leads with Status.
        builder.HasOne(a => a.ClaimedBy)
            .WithMany()
            .HasForeignKey(a => a.ClaimedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.Status, a.ClaimedByUserId });
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_RentalApplications_Claim",
            "([Status] = 'UnderReview' AND [ClaimedByUserId] IS NOT NULL AND [ClaimedAt] IS NOT NULL) OR " +
            "([Status] <> 'UnderReview' AND [ClaimedByUserId] IS NULL AND [ClaimedAt] IS NULL)"));

        builder.HasMany(a => a.Residences)
            .WithOne(r => r.RentalApplication)
            .HasForeignKey(r => r.RentalApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(a => a.StatusHistory)
            .WithOne(h => h.RentalApplication)
            .HasForeignKey(h => h.RentalApplicationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps prior residences.</summary>
internal sealed class ResidenceConfiguration : IEntityTypeConfiguration<Residence>
{
    /// <summary>
    /// Maps <see cref="Residence"/>: a database check that move-out is not before move-in, the required owned address,
    /// and landlord contact lengths.
    /// </summary>
    /// <param name="builder">The entity builder supplied by EF Core.</param>
    public void Configure(EntityTypeBuilder<Residence> builder)
    {
        builder.ToTable(t => t.HasCheckConstraint("CK_Residences_MoveDates", "[MoveOutDate] >= [MoveInDate]"));

        builder.OwnsOne(r => r.Address, a => AddressMapping.Map(a, string.Empty));
        builder.Navigation(r => r.Address).IsRequired();
        builder.Property(r => r.LandlordName).HasMaxLength(150).IsRequired();
        builder.Property(r => r.LandlordPhone).HasMaxLength(30).IsRequired();
    }
}

/// <summary>Maps the status history (audit trail) table.</summary>
internal sealed class ApplicationStatusHistoryConfiguration : IEntityTypeConfiguration<ApplicationStatusHistory>
{
    /// <summary>
    /// Maps <see cref="ApplicationStatusHistory"/> to the singular table name "ApplicationStatusHistory", limits the
    /// comment length, indexes entries per application in time order, and restricts the link to the user who made the
    /// change (deleting a user must not erase history).
    /// </summary>
    /// <param name="builder">The entity builder supplied by EF Core.</param>
    public void Configure(EntityTypeBuilder<ApplicationStatusHistory> builder)
    {
        builder.ToTable("ApplicationStatusHistory");
        builder.Property(h => h.Comment).HasMaxLength(2000);
        builder.HasIndex(h => new { h.RentalApplicationId, h.ChangedAt });

        builder.HasOne(h => h.ChangedBy)
            .WithMany()
            .HasForeignKey(h => h.ChangedById)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Maps leases and their one-to-one link to the approved application.</summary>
internal sealed class LeaseConfiguration : IEntityTypeConfiguration<Lease>
{
    /// <summary>
    /// Maps <see cref="Lease"/>: a database check that the term ends on or after it starts, an index for the
    /// active-lease lookup (unit + date range), a restricted link to the unit, and a one-to-one link to the application
    /// whose unique foreign key guarantees one lease per application.
    /// </summary>
    /// <param name="builder">The entity builder supplied by EF Core.</param>
    public void Configure(EntityTypeBuilder<Lease> builder)
    {
        builder.ToTable(t => t.HasCheckConstraint("CK_Leases_Term", "[EndDate] >= [StartDate]"));

        // The active-lease check looks up a unit's leases by date range.
        builder.HasIndex(l => new { l.UnitId, l.StartDate, l.EndDate });

        builder.HasOne(l => l.Unit)
            .WithMany(u => u.Leases)
            .HasForeignKey(l => l.UnitId)
            .OnDelete(DeleteBehavior.Restrict);

        // One lease per application, enforced by the unique index EF creates for a one-to-one FK.
        builder.HasOne(l => l.RentalApplication)
            .WithOne(a => a.Lease)
            .HasForeignKey<Lease>(l => l.RentalApplicationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
