// =====================================================================================================================
// Module: RentalManagement.Tests / Domain / LeaseAndUnitTypeTests.cs
// Purpose: Unit tests of two domain rules: the twelve-month lease term (inclusive dates, leap years, which days a
//          lease covers, issuing only on approval) and the unit-type rule (an inactive type stays only where used).
// =====================================================================================================================

using RentalManagement.Core.Domain;

namespace RentalManagement.Tests.Domain;

/// <summary>Tests of <see cref="Lease"/> and <see cref="UnitType.CanBeAssignedTo"/>.</summary>
public class LeaseAndUnitTypeTests
{
    /// <summary>The term ends the day before the first anniversary, including across and on leap days.</summary>
    [Theory]
    [InlineData("2026-01-01", "2026-12-31")]
    [InlineData("2026-03-15", "2027-03-14")]
    [InlineData("2027-03-01", "2028-02-29")] // ends on a leap day
    [InlineData("2028-02-29", "2029-02-28")] // starts on a leap day: anniversary is Mar 1, so the term ends Feb 28
    [InlineData("2026-01-31", "2027-01-30")] // month end
    [InlineData("2026-12-31", "2027-12-30")] // year end
    [InlineData("2026-03-01", "2027-02-28")] // ends the day before a non-leap March 1
    public void Lease_term_is_twelve_months_inclusive(string start, string expectedEnd) =>
        Assert.Equal(DateOnly.Parse(expectedEnd), Lease.EndDateFor(DateOnly.Parse(start)));

    /// <summary>A lease covers its first and last day and everything between, and nothing outside.</summary>
    [Theory]
    [InlineData("2025-12-31", false)]
    [InlineData("2026-01-01", true)]
    [InlineData("2026-07-01", true)]
    [InlineData("2026-12-31", true)]
    [InlineData("2027-01-01", false)]
    public void Lease_covers_only_days_inside_its_term(string day, bool covered)
    {
        var lease = new Lease { StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31) };

        Assert.Equal(covered, lease.Covers(DateOnly.Parse(day)));
    }

    /// <summary>Issuing a lease for a draft is a programming error and throws.</summary>
    [Fact]
    public void Lease_can_only_be_issued_for_an_approved_application()
    {
        var application = RentalApplication.StartDraft(1, "applicant", DateTimeOffset.UnixEpoch);

        Assert.Throws<InvalidOperationException>(() =>
            Lease.Issue(application, 1200m, new DateOnly(2026, 1, 1), DateTimeOffset.UnixEpoch));
    }

    /// <summary>The issued lease takes the application's unit, the given rent, and a twelve-month term.</summary>
    [Fact]
    public void Issued_lease_copies_unit_and_rent_and_runs_twelve_months()
    {
        var application = RentalApplication.StartDraft(7, "applicant", DateTimeOffset.UnixEpoch);
        application.TransitionTo(ApplicationStatus.Submitted, "applicant", DateTimeOffset.UnixEpoch);
        application.Claim("manager", DateTimeOffset.UnixEpoch);
        application.TransitionTo(ApplicationStatus.Approved, "manager", DateTimeOffset.UnixEpoch, ReviewOutcome.Approve);

        var lease = Lease.Issue(application, 1450m, new DateOnly(2026, 5, 1), DateTimeOffset.UnixEpoch);

        Assert.Equal(7, lease.UnitId);
        Assert.Equal(1450m, lease.MonthlyRent);
        Assert.Equal(new DateOnly(2027, 4, 30), lease.EndDate);
    }

    /// <summary>An active type is assignable to a new unit and to any existing unit.</summary>
    [Fact]
    public void Active_unit_type_can_be_assigned_to_any_unit()
    {
        var type = new UnitType { Id = 1, IsActive = true };

        Assert.True(type.CanBeAssignedTo(null));
        Assert.True(type.CanBeAssignedTo(2));
    }

    /// <summary>An inactive type may stay on the unit that has it, but no other unit (new or existing) may take it.</summary>
    [Fact]
    public void Inactive_unit_type_stays_on_its_unit_but_cannot_be_assigned_elsewhere()
    {
        var type = new UnitType { Id = 5, IsActive = false };

        Assert.True(type.CanBeAssignedTo(5));   // the unit already uses it
        Assert.False(type.CanBeAssignedTo(2));  // another unit switching to it
        Assert.False(type.CanBeAssignedTo(null)); // a new unit
    }
}
