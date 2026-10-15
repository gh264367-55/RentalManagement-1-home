// =====================================================================================================================
// Module: RentalManagement.Core / Services / LeaseQueries.cs
// Purpose: IQueryable extension methods expressing the active-lease rule in a form EF Core translates to SQL, so
//          "is this unit leased today?" is answered in the database for availability lists, submit and approval.
// =====================================================================================================================

using RentalManagement.Core.Domain;

namespace RentalManagement.Core.Services;

/// <summary>
/// The "active lease" rule as composable, SQL-translatable filters, so availability, submit and approval all ask the
/// same question the same way: does a lease on this unit cover the given day?
/// </summary>
public static class LeaseQueries
{
    /// <summary>Leases whose term covers <paramref name="day"/> (start and end inclusive). Mirrors <see cref="Lease.Covers"/>.</summary>
    /// <param name="leases">The leases to filter.</param>
    /// <param name="day">The day to test, usually today.</param>
    /// <returns>The filtered query.</returns>
    public static IQueryable<Lease> CoveringDay(this IQueryable<Lease> leases, DateOnly day) =>
        leases.Where(l => l.StartDate <= day && l.EndDate >= day);

    /// <summary>Units with no lease covering <paramref name="day"/>, i.e. units an applicant can apply for.</summary>
    /// <param name="units">The units to filter.</param>
    /// <param name="day">The day to test, usually today.</param>
    /// <returns>The filtered query.</returns>
    public static IQueryable<Unit> AvailableOn(this IQueryable<Unit> units, DateOnly day) =>
        units.Where(u => !u.Leases.Any(l => l.StartDate <= day && l.EndDate >= day));
}
