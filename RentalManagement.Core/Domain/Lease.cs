// =====================================================================================================================
// Module: RentalManagement.Core / Domain / Lease.cs
// Purpose: The twelve-month lease issued when an application is approved. Owns the lease-term date arithmetic
//          (inclusive end date, leap-day handling) and the "does this lease cover a given day" rule that decides
//          whether a unit is available. The SQL-side equivalent of Covers lives in Services/LeaseQueries.cs.
// =====================================================================================================================

namespace RentalManagement.Core.Domain;

/// <summary>A twelve-month lease issued when an application is approved.</summary>
public class Lease
{
    /// <summary>The lease term required by the assessment.</summary>
    public const int TermMonths = 12;

    public int Id { get; set; }

    public int UnitId { get; set; }

    public Unit Unit { get; set; } = null!;

    /// <summary>The approved application that produced this lease (one lease per application).</summary>
    public int RentalApplicationId { get; set; }

    public RentalApplication RentalApplication { get; set; } = null!;

    public DateOnly StartDate { get; set; }

    /// <summary>Inclusive last day: <c>StartDate + 12 months − 1 day</c>.</summary>
    public DateOnly EndDate { get; set; }

    /// <summary>The unit's rent when the lease was issued. Later rent changes don't alter a signed lease.</summary>
    public decimal MonthlyRent { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The inclusive end date of a lease that starts on <paramref name="start"/>.</summary>
    public static DateOnly EndDateFor(DateOnly start)
    {
        var anniversary = start.AddMonths(TermMonths);

        // AddMonths clamps Feb 29 to Feb 28 in a non-leap year; the anniversary is then Mar 1, so the term ends Feb 28.
        if (anniversary.Day != start.Day)
        {
            anniversary = anniversary.AddDays(1);
        }

        return anniversary.AddDays(-1);
    }

    /// <summary>True when <paramref name="day"/> falls inside the lease term. Such a unit is not available.</summary>
    public bool Covers(DateOnly day) => StartDate <= day && day <= EndDate;

    /// <summary>Creates the lease for an approved application, starting on <paramref name="start"/>.</summary>
    public static Lease Issue(RentalApplication application, decimal monthlyRent, DateOnly start, DateTimeOffset now)
    {
        if (application.Status != ApplicationStatus.Approved)
        {
            throw new InvalidOperationException(
                $"A lease can only be issued for an approved application (was {application.Status}).");
        }

        return new Lease
        {
            UnitId = application.UnitId,
            RentalApplication = application,
            StartDate = start,
            EndDate = EndDateFor(start),
            MonthlyRent = monthlyRent,
            CreatedAt = now,
        };
    }
}
