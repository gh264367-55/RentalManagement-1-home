// =====================================================================================================================
// Module: RentalManagement.Tests / Services / ApplicationReviewServiceTests.cs
// Purpose: Tests of the property manager's review workflow in ApplicationReviewService against an EF Core in-memory
//          database and a fake clock: the three outcomes and their comment rules, lease issuing and start-date rules,
//          the active/overlapping-lease check, leaving other applications untouched, terminal statuses, reviewer
//          permission, claiming (incl. two managers racing for one claim), releasing, "only the claim owner reviews",
//          the status history of claims / releases / outcomes, and two approvals racing for the same unit.
// =====================================================================================================================

using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using RentalManagement.Core.Data;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services;
using RentalManagement.Core.Services.Applications;

namespace RentalManagement.Tests.Services;

/// <summary>
/// Tests of <see cref="ApplicationReviewService"/>. Each test gets a fresh database with a property manager, two
/// applicants (Alice, Bob) and a property with two units.
/// </summary>
public sealed class ApplicationReviewServiceTests : IDisposable
{
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Manager = "manager";
    private const string SecondManager = "manager2";
    private static readonly DateOnly Today = new(2026, 10, 8);

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly ApplicationDbContext _db;
    private readonly ApplicationReviewService _service;
    private int _unitId;
    private int _otherUnitId;

    /// <summary>Set by <see cref="UseSqliteAsync"/>: an open connection that keeps the shared in-memory SQLite database alive.</summary>
    private SqliteConnection? _sqliteKeepAlive;

    /// <summary>Creates the per-test in-memory database with roles, users, a property and two units.</summary>
    public ApplicationReviewServiceTests()
    {
        _clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        _db = NewContext();
        _service = new ApplicationReviewService(_db, _clock);
        Seed(_db);
    }

    /// <summary>Disposes the test's DbContext and, if used, the SQLite database.</summary>
    public void Dispose()
    {
        _db.Dispose();
        _sqliteKeepAlive?.Dispose();
    }

    /// <summary>
    /// Adds the test data: both roles, applicants Alice and Bob, two property managers, and a property with units
    /// "101" and "102" (whose ids are remembered).
    /// </summary>
    private void Seed(ApplicationDbContext db)
    {
        var managerRole = new IdentityRole(Roles.PropertyManager) { Id = "role-pm", NormalizedName = "PROPERTYMANAGER" };
        var applicantRole = new IdentityRole(Roles.Applicant) { Id = "role-app", NormalizedName = "APPLICANT" };
        db.Roles.AddRange(managerRole, applicantRole);
        db.Users.AddRange(
            new ApplicationUser { Id = Alice, UserName = "alice@test", Email = "alice@test", FirstName = "Alice", LastName = "Archer" },
            new ApplicationUser { Id = Bob, UserName = "bob@test", Email = "bob@test", FirstName = "Bob", LastName = "Baker" },
            new ApplicationUser { Id = Manager, UserName = "pm@test", Email = "pm@test", FirstName = "Pat", LastName = "Manager" },
            new ApplicationUser { Id = SecondManager, UserName = "pm2@test", Email = "pm2@test", FirstName = "Sam", LastName = "Second" });
        db.UserRoles.AddRange(
            new IdentityUserRole<string> { UserId = Alice, RoleId = applicantRole.Id },
            new IdentityUserRole<string> { UserId = Bob, RoleId = applicantRole.Id },
            new IdentityUserRole<string> { UserId = Manager, RoleId = managerRole.Id },
            new IdentityUserRole<string> { UserId = SecondManager, RoleId = managerRole.Id });

        var type = new UnitType { Name = "Apartment", IsActive = true };
        var property = new Property
        {
            Name = "Maple Court",
            Address = new Address { Street = "1 Maple St", City = "Troy", State = "NY", PostalCode = "12180" },
        };
        var unit = new Unit { UnitNumber = "101", Bedrooms = 1, MonthlyRent = 1250m, UnitType = type };
        var otherUnit = new Unit { UnitNumber = "102", Bedrooms = 2, MonthlyRent = 1500m, UnitType = type };
        property.Units.Add(unit);
        property.Units.Add(otherUnit);
        db.Properties.Add(property);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        (_unitId, _otherUnitId) = (unit.Id, otherUnit.Id);
    }

    /// <summary>
    /// Switches the test to a shared in-memory SQLite database. Used where atomic saves matter: SQLite, like SQL
    /// Server, runs SaveChanges in a transaction, while EF's in-memory provider may apply part of a save that then
    /// fails its concurrency check.
    /// </summary>
    private async Task UseSqliteAsync()
    {
        _sqliteConnectionString = $"Data Source=review-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _sqliteKeepAlive = new SqliteConnection(_sqliteConnectionString);
        await _sqliteKeepAlive.OpenAsync();
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        Seed(db);
    }

    /// <summary>The SQLite connection string once <see cref="UseSqliteAsync"/> has run; null means the in-memory provider.</summary>
    private string? _sqliteConnectionString;

    // ---- Outcomes -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Approve moves the application to Approved and issues a twelve-month lease from the chosen start date at the
    /// unit's rent, recording reviewer, time, outcome and comment in the history.
    /// </summary>
    [Fact]
    public async Task Approve_issues_a_twelve_month_lease_and_records_history()
    {
        var id = await ClaimedAsync(_unitId, Alice);
        var start = Today.AddDays(14);

        var result = await _service.ReviewAsync(id, Manager, new ReviewInput(ReviewOutcome.Approve, " Welcome! ", start));

        Assert.True(result.Succeeded, result.Error);
        var application = await LoadAsync(id);
        Assert.Equal(ApplicationStatus.Approved, application.Status);
        Assert.NotNull(application.Lease);
        Assert.Equal((_unitId, start, new DateOnly(2027, 10, 21), 1250m),
            (application.Lease!.UnitId, application.Lease.StartDate, application.Lease.EndDate, application.Lease.MonthlyRent));
        var last = application.StatusHistory.OrderBy(h => h.Id).Last();
        Assert.Equal((ApplicationStatus.UnderReview, ApplicationStatus.Approved, ReviewOutcome.Approve, "Welcome!", Manager, _clock.GetUtcNow()),
            (last.FromStatus, last.ToStatus, last.Outcome, last.Comment, last.ChangedById, last.ChangedAt));
    }

    /// <summary>Approve doesn't need a comment.</summary>
    [Fact]
    public async Task Approve_comment_is_optional()
    {
        var id = await ClaimedAsync(_unitId, Alice);

        var result = await _service.ReviewAsync(id, Manager, new ReviewInput(ReviewOutcome.Approve, null, Today));

        Assert.True(result.Succeeded, result.Error);
    }

    /// <summary>Return and Deny are refused without a comment (on the Comment field) and nothing changes.</summary>
    [Theory]
    [InlineData(ReviewOutcome.Return, null)]
    [InlineData(ReviewOutcome.Return, "   ")]
    [InlineData(ReviewOutcome.Deny, null)]
    [InlineData(ReviewOutcome.Deny, "")]
    public async Task Return_and_deny_require_a_comment(ReviewOutcome outcome, string? comment)
    {
        var id = await ClaimedAsync(_unitId, Alice);

        var result = await _service.ReviewAsync(id, Manager, new ReviewInput(outcome, comment, null));

        Assert.False(result.Succeeded);
        Assert.Equal(FailureKind.Validation, result.Kind);
        Assert.Equal(nameof(ReviewInput.Comment), result.Field);
        Assert.Equal(ApplicationStatus.UnderReview, (await LoadAsync(id)).Status);
    }

    /// <summary>Return sends the application back for correction (editable again) with the comment in the history.</summary>
    [Fact]
    public async Task Return_sends_the_application_back_to_the_applicant()
    {
        var id = await ClaimedAsync(_unitId, Alice);

        var result = await _service.ReviewAsync(id, Manager, new ReviewInput(ReviewOutcome.Return, "Add your landlord's phone.", null));

        Assert.True(result.Succeeded, result.Error);
        var application = await LoadAsync(id);
        Assert.Equal(ApplicationStatus.Returned, application.Status);
        Assert.True(ApplicationService.CanApplicantEdit(application.Status, isOwner: true));
        Assert.Null(application.Lease);
        Assert.Equal("Add your landlord's phone.", application.StatusHistory.OrderBy(h => h.Id).Last().Comment);
    }

    /// <summary>Deny closes the application for good: Denied is terminal and no lease is issued.</summary>
    [Fact]
    public async Task Deny_is_terminal()
    {
        var id = await ClaimedAsync(_unitId, Alice);

        var result = await _service.ReviewAsync(id, Manager, new ReviewInput(ReviewOutcome.Deny, "References not verified.", null));

        Assert.True(result.Succeeded, result.Error);
        var application = await LoadAsync(id);
        Assert.Equal(ApplicationStatus.Denied, application.Status);
        Assert.True(ApplicationStateMachine.IsTerminal(application.Status));
        Assert.Null(application.Lease);
    }

    /// <summary>A missing outcome is refused on the Outcome field.</summary>
    [Fact]
    public async Task An_outcome_must_be_chosen()
    {
        var id = await ClaimedAsync(_unitId, Alice);

        var result = await _service.ReviewAsync(id, Manager, new ReviewInput(null, "Hmm", null));

        Assert.Equal(nameof(ReviewInput.Outcome), result.Field);
    }

    /// <summary>Approve needs a lease start date that isn't in the past or more than a year ahead.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(366)]
    public async Task Approve_needs_a_valid_lease_start_date(int? daysFromToday)
    {
        var id = await ClaimedAsync(_unitId, Alice);
        DateOnly? start = daysFromToday is { } days ? Today.AddDays(days) : null;

        var result = await _service.ReviewAsync(id, Manager, new ReviewInput(ReviewOutcome.Approve, null, start));

        Assert.False(result.Succeeded);
        Assert.Equal(nameof(ReviewInput.LeaseStartDate), result.Field);
        Assert.Equal(ApplicationStatus.UnderReview, (await LoadAsync(id)).Status);
    }

    /// <summary>The start-date limits are inclusive: today and exactly 365 days ahead are both accepted.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(365)]
    public async Task Lease_start_date_limits_are_inclusive(int daysFromToday)
    {
        var id = await ClaimedAsync(_unitId, Alice);
        var start = Today.AddDays(daysFromToday);

        var result = await _service.ReviewAsync(id, Manager, new ReviewInput(ReviewOutcome.Approve, null, start));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(start, (await LoadAsync(id)).Lease!.StartDate);
    }

    /// <summary>
    /// A lease that ends the day before the new one starts doesn't overlap (end dates are inclusive), so back-to-back
    /// leases are allowed; one that ends on the new start day does overlap.
    /// </summary>
    [Theory]
    [InlineData(1, true)]   // existing lease ends the day before the new start → no overlap
    [InlineData(0, false)]  // existing lease ends on the start day → overlap
    public async Task Back_to_back_leases_are_allowed_but_a_shared_day_is_not(int gapDays, bool approved)
    {
        // Existing lease: starts so that its inclusive end is (newStart - gapDays).
        var newStart = Today.AddDays(30);
        var existingEnd = newStart.AddDays(-gapDays);
        await ApprovedWithLeaseAsync(_unitId, Bob, start: existingEnd.AddMonths(-12).AddDays(1));
        var id = await ClaimedAsync(_unitId, Alice);

        var result = await _service.ReviewAsync(id, Manager, new ReviewInput(ReviewOutcome.Approve, null, newStart));

        Assert.Equal(approved, result.Succeeded);
    }

    // ---- Which applications can be reviewed, and by whom --------------------------------------------------------

    /// <summary>
    /// Only applications Under Review can be reviewed: a Submitted one (not claimed yet) and every other status is a
    /// Conflict and stays unchanged.
    /// </summary>
    [Theory]
    [InlineData(ApplicationStatus.Draft)]
    [InlineData(ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.Returned)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Denied)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public async Task Only_applications_under_review_can_be_reviewed(ApplicationStatus status)
    {
        var id = await InStatusAsync(status);

        var result = await _service.ReviewAsync(id, Manager, new ReviewInput(ReviewOutcome.Deny, "No.", null));

        Assert.Equal(FailureKind.Conflict, result.Kind);
        Assert.Equal(status, (await LoadAsync(id)).Status);
    }

    /// <summary>The service itself refuses reviewers who aren't property managers, whatever the caller checked.</summary>
    [Fact]
    public async Task Applicants_cannot_review()
    {
        var id = await ClaimedAsync(_unitId, Alice);

        var own = await _service.ReviewAsync(id, Alice, new ReviewInput(ReviewOutcome.Approve, null, Today));
        var other = await _service.ReviewAsync(id, Bob, new ReviewInput(ReviewOutcome.Approve, null, Today));

        Assert.Equal(FailureKind.NotFound, own.Kind);
        Assert.Equal(FailureKind.NotFound, other.Kind);
        Assert.Equal(ApplicationStatus.UnderReview, (await LoadAsync(id)).Status);
    }

    /// <summary>An unknown application id is NotFound.</summary>
    [Fact]
    public async Task Unknown_application_is_not_found()
    {
        var result = await _service.ReviewAsync(999, Manager, new ReviewInput(ReviewOutcome.Approve, null, Today));

        Assert.Equal(FailureKind.NotFound, result.Kind);
    }

    // ---- Leases -------------------------------------------------------------------------------------------------

    /// <summary>Approval is refused while the unit has a lease covering today; the application stays Submitted.</summary>
    [Fact]
    public async Task Approve_is_refused_when_the_unit_has_an_active_lease()
    {
        await ApprovedWithLeaseAsync(_unitId, Bob, start: Today.AddMonths(-3));
        var id = await ClaimedAsync(_unitId, Alice);

        var result = await _service.ReviewAsync(id, Manager, new ReviewInput(ReviewOutcome.Approve, null, Today));

        Assert.Equal(FailureKind.Conflict, result.Kind);
        Assert.Contains("active lease", result.Error);
        Assert.Equal(ApplicationStatus.UnderReview, (await LoadAsync(id)).Status);
        Assert.Equal(1, await CountLeasesAsync(_unitId));
    }

    /// <summary>A future lease that overlaps the new term also blocks approval (never two leases at once).</summary>
    [Fact]
    public async Task Approve_is_refused_when_a_future_lease_overlaps()
    {
        await ApprovedWithLeaseAsync(_unitId, Bob, start: Today.AddMonths(2));
        var id = await ClaimedAsync(_unitId, Alice);

        var result = await _service.ReviewAsync(id, Manager, new ReviewInput(ReviewOutcome.Approve, null, Today));

        Assert.Equal(FailureKind.Conflict, result.Kind);
        Assert.Equal(1, await CountLeasesAsync(_unitId));
    }

    /// <summary>An expired lease doesn't block a new one.</summary>
    [Fact]
    public async Task Expired_lease_does_not_block_approval()
    {
        await ApprovedWithLeaseAsync(_unitId, Bob, start: Today.AddMonths(-14));
        var id = await ClaimedAsync(_unitId, Alice);

        var result = await _service.ReviewAsync(id, Manager, new ReviewInput(ReviewOutcome.Approve, null, Today));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, await CountLeasesAsync(_unitId));
    }

    /// <summary>
    /// Approving one of two open applications for a unit leaves the other Submitted and untouched; a later attempt to
    /// approve it is refused by the lease check, while Return/Deny still work on it.
    /// </summary>
    [Fact]
    public async Task Other_open_applications_for_the_unit_are_left_unchanged()
    {
        var alices = await ClaimedAsync(_unitId, Alice);
        var bobs = await ClaimedAsync(_unitId, Bob);
        var bobsBefore = await LoadAsync(bobs);

        Assert.True((await _service.ReviewAsync(alices, Manager, new ReviewInput(ReviewOutcome.Approve, null, Today))).Succeeded);

        var bobsAfter = await LoadAsync(bobs);
        Assert.Equal(ApplicationStatus.UnderReview, bobsAfter.Status);
        Assert.Equal(bobsBefore.ConcurrencyStamp, bobsAfter.ConcurrencyStamp);
        Assert.Equal(bobsBefore.StatusHistory.Count, bobsAfter.StatusHistory.Count);

        var secondApproval = await _service.ReviewAsync(bobs, Manager, new ReviewInput(ReviewOutcome.Approve, null, Today));
        Assert.Equal(FailureKind.Conflict, secondApproval.Kind);
        Assert.Equal(1, await CountLeasesAsync(_unitId));

        var deny = await _service.ReviewAsync(bobs, Manager, new ReviewInput(ReviewOutcome.Deny, "Unit has been leased.", null));
        Assert.True(deny.Succeeded, deny.Error);
    }

    /// <summary>Approvals on different units don't interfere with each other.</summary>
    [Fact]
    public async Task Approvals_on_different_units_are_independent()
    {
        var a = await ClaimedAsync(_unitId, Alice);
        var b = await ClaimedAsync(_otherUnitId, Bob);

        Assert.True((await _service.ReviewAsync(a, Manager, new ReviewInput(ReviewOutcome.Approve, null, Today))).Succeeded);
        Assert.True((await _service.ReviewAsync(b, Manager, new ReviewInput(ReviewOutcome.Approve, null, Today))).Succeeded);
    }

    // ---- Concurrency --------------------------------------------------------------------------------------------

    /// <summary>
    /// Worst-case race: manager 2 has loaded the unit and passed the lease check for Bob's application when manager 1
    /// approves Alice's application for the same unit and commits. Manager 2's save then fails on the unit's
    /// concurrency stamp: exactly one lease exists and Bob's application is still Submitted. Runs on SQLite so the
    /// failed save is rolled back as a whole, as it is on SQL Server.
    /// </summary>
    [Fact]
    public async Task Concurrent_approvals_for_the_same_unit_cannot_both_create_leases()
    {
        await UseSqliteAsync();
        var alices = await ClaimedAsync(_unitId, Alice);
        var bobs = await ClaimedAsync(_unitId, Bob, SecondManager);
        OperationResult? firstResult = null;

        // Runs manager 1's whole approval at the moment manager 2 is about to save.
        var interleave = new BeforeFirstSave(async () =>
        {
            await using var firstDb = NewContext();
            firstResult = await new ApplicationReviewService(firstDb, _clock)
                .ReviewAsync(alices, Manager, new ReviewInput(ReviewOutcome.Approve, null, Today));
        });
        await using var secondDb = NewContext(interleave);
        var secondResult = await new ApplicationReviewService(secondDb, _clock)
            .ReviewAsync(bobs, SecondManager, new ReviewInput(ReviewOutcome.Approve, null, Today));

        Assert.True(firstResult!.Succeeded, firstResult.Error);
        Assert.False(secondResult.Succeeded);
        Assert.Equal(FailureKind.Conflict, secondResult.Kind);
        Assert.Equal(1, await CountLeasesAsync(_unitId));
        Assert.Equal(ApplicationStatus.Approved, (await LoadAsync(alices)).Status);
        Assert.Equal(ApplicationStatus.UnderReview, (await LoadAsync(bobs)).Status);
    }

    /// <summary>
    /// The claim owner reviewing the same application in two browser tabs at once: the second save is refused as a
    /// whole (no status change, no lease), not applied on top of the first. Runs on SQLite for transactional saves.
    /// </summary>
    [Fact]
    public async Task Concurrent_reviews_of_the_same_application_cannot_both_apply()
    {
        await UseSqliteAsync();
        var id = await ClaimedAsync(_unitId, Alice);
        OperationResult? firstResult = null;

        var interleave = new BeforeFirstSave(async () =>
        {
            await using var firstDb = NewContext();
            firstResult = await new ApplicationReviewService(firstDb, _clock)
                .ReviewAsync(id, Manager, new ReviewInput(ReviewOutcome.Return, "Fix the phone number.", null));
        });
        await using var secondDb = NewContext(interleave);
        var secondResult = await new ApplicationReviewService(secondDb, _clock)
            .ReviewAsync(id, Manager, new ReviewInput(ReviewOutcome.Approve, null, Today));

        Assert.True(firstResult!.Succeeded, firstResult.Error);
        Assert.Equal(FailureKind.Conflict, secondResult.Kind);
        var application = await LoadAsync(id);
        Assert.Equal(ApplicationStatus.Returned, application.Status);
        Assert.Null(application.Lease);
    }

    // ---- Claim ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A manager claims a Submitted application: it becomes Under Review with that manager as claim owner, the clock's
    /// time as claim time, and a history row (Submitted → Under Review, by the manager, no outcome).
    /// </summary>
    [Fact]
    public async Task Claim_moves_a_submitted_application_to_under_review_and_records_owner_and_time()
    {
        var id = await SubmittedAsync(_unitId, Alice);

        var result = await _service.ClaimAsync(id, Manager);

        Assert.True(result.Succeeded, result.Error);
        var application = await LoadAsync(id);
        Assert.Equal(ApplicationStatus.UnderReview, application.Status);
        Assert.Equal((Manager, (DateTimeOffset?)_clock.GetUtcNow()), (application.ClaimedByUserId, application.ClaimedAt));
        var last = application.StatusHistory.OrderBy(h => h.Id).Last();
        Assert.Equal((ApplicationStatus.Submitted, ApplicationStatus.UnderReview, Manager, (ReviewOutcome?)null, _clock.GetUtcNow()),
            (last.FromStatus, last.ToStatus, last.ChangedById, last.Outcome, last.ChangedAt));
    }

    /// <summary>Applicants can't claim (their own or anyone's application); the service refuses whatever the caller checked.</summary>
    [Fact]
    public async Task Non_managers_cannot_claim()
    {
        var id = await SubmittedAsync(_unitId, Alice);

        var own = await _service.ClaimAsync(id, Alice);
        var other = await _service.ClaimAsync(id, Bob);

        Assert.Equal(FailureKind.NotFound, own.Kind);
        Assert.Equal(FailureKind.NotFound, other.Kind);
        var application = await LoadAsync(id);
        Assert.Equal(ApplicationStatus.Submitted, application.Status);
        Assert.Null(application.ClaimedByUserId);
    }

    /// <summary>
    /// A claimed application can't be claimed again: another manager gets a Conflict naming the claim owner, and the
    /// claim is never overwritten; the owner trying again is told they already hold it.
    /// </summary>
    [Fact]
    public async Task A_claimed_application_cannot_be_claimed_again()
    {
        var id = await ClaimedAsync(_unitId, Alice);
        var historyBefore = (await LoadAsync(id)).StatusHistory.Count;

        var byOther = await _service.ClaimAsync(id, SecondManager);
        var byOwner = await _service.ClaimAsync(id, Manager);

        Assert.Equal(FailureKind.Conflict, byOther.Kind);
        Assert.Contains("Pat Manager", byOther.Error);
        Assert.Equal(FailureKind.Conflict, byOwner.Kind);
        var application = await LoadAsync(id);
        Assert.Equal(Manager, application.ClaimedByUserId);
        Assert.Equal(historyBefore, application.StatusHistory.Count);
    }

    /// <summary>Only Submitted applications can be claimed: drafts, returned ones and terminal ones are a Conflict.</summary>
    [Theory]
    [InlineData(ApplicationStatus.Draft)]
    [InlineData(ApplicationStatus.Returned)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Denied)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public async Task Only_submitted_applications_can_be_claimed(ApplicationStatus status)
    {
        var id = await InStatusAsync(status);

        var result = await _service.ClaimAsync(id, SecondManager);

        Assert.Equal(FailureKind.Conflict, result.Kind);
        var application = await LoadAsync(id);
        Assert.Equal(status, application.Status);
        Assert.Null(application.ClaimedByUserId);
    }

    /// <summary>An unknown application id is NotFound.</summary>
    [Fact]
    public async Task Claiming_an_unknown_application_is_not_found() =>
        Assert.Equal(FailureKind.NotFound, (await _service.ClaimAsync(999, Manager)).Kind);

    /// <summary>
    /// Two managers claim the same application at the same moment (both have read it as Submitted and unclaimed):
    /// exactly one claim commits; the other gets a Conflict and none of it (status, owner, history row) is saved.
    /// Runs on SQLite, whose SaveChanges is transactional like SQL Server's; the SQL Server version of this race is in
    /// <see cref="ReviewQueueSqlServerTests"/>.
    /// </summary>
    [Fact]
    public async Task Two_managers_claiming_at_the_same_moment_cannot_both_succeed()
    {
        await UseSqliteAsync();
        var id = await SubmittedAsync(_unitId, Alice);
        OperationResult? firstResult = null;

        // Manager 1's whole claim runs at the moment manager 2 is about to save its own.
        var interleave = new BeforeFirstSave(async () =>
        {
            await using var firstDb = NewContext();
            firstResult = await new ApplicationReviewService(firstDb, _clock).ClaimAsync(id, Manager);
        });
        await using var secondDb = NewContext(interleave);
        var secondResult = await new ApplicationReviewService(secondDb, _clock).ClaimAsync(id, SecondManager);

        Assert.True(firstResult!.Succeeded, firstResult.Error);
        Assert.Equal(FailureKind.Conflict, secondResult.Kind);
        var application = await LoadAsync(id);
        Assert.Equal((ApplicationStatus.UnderReview, Manager), (application.Status, application.ClaimedByUserId));
        Assert.Single(application.StatusHistory, h => h.ToStatus == ApplicationStatus.UnderReview);
    }

    // ---- Release --------------------------------------------------------------------------------------------------

    /// <summary>
    /// The claim owner releases: back to Submitted, claim cleared, original submission date kept, a history row
    /// (Under Review → Submitted, by the manager), and another manager can then claim it.
    /// </summary>
    [Fact]
    public async Task Claim_owner_can_release_and_another_manager_can_then_claim()
    {
        var id = await SubmittedAsync(_unitId, Alice);
        var submittedAt = (await LoadAsync(id)).SubmittedAt;
        Assert.True((await _service.ClaimAsync(id, Manager)).Succeeded);
        _clock.Advance(TimeSpan.FromHours(2));

        var result = await _service.ReleaseAsync(id, Manager);

        Assert.True(result.Succeeded, result.Error);
        var application = await LoadAsync(id);
        Assert.Equal(ApplicationStatus.Submitted, application.Status);
        Assert.Null(application.ClaimedByUserId);
        Assert.Null(application.ClaimedAt);
        Assert.Equal(submittedAt, application.SubmittedAt);
        var last = application.StatusHistory.OrderBy(h => h.Id).Last();
        Assert.Equal((ApplicationStatus.UnderReview, ApplicationStatus.Submitted, Manager), (last.FromStatus, last.ToStatus, last.ChangedById));

        Assert.True((await _service.ClaimAsync(id, SecondManager)).Succeeded);
        Assert.Equal(SecondManager, (await LoadAsync(id)).ClaimedByUserId);
    }

    /// <summary>Another manager can't release someone else's claim, and an applicant can't release at all.</summary>
    [Fact]
    public async Task Only_the_claim_owner_can_release()
    {
        var id = await ClaimedAsync(_unitId, Alice);

        var byOther = await _service.ReleaseAsync(id, SecondManager);
        var byApplicant = await _service.ReleaseAsync(id, Alice);

        Assert.Equal(FailureKind.Conflict, byOther.Kind);
        Assert.Contains("Only the manager who claimed it", byOther.Error);
        Assert.Equal(FailureKind.NotFound, byApplicant.Kind);
        var application = await LoadAsync(id);
        Assert.Equal((ApplicationStatus.UnderReview, Manager), (application.Status, application.ClaimedByUserId));
    }

    /// <summary>An application that isn't claimed (Submitted) or isn't under review any more has nothing to release.</summary>
    [Theory]
    [InlineData(ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.Returned)]
    [InlineData(ApplicationStatus.Approved)]
    public async Task Release_needs_an_application_under_review(ApplicationStatus status)
    {
        var id = await InStatusAsync(status);

        var result = await _service.ReleaseAsync(id, Manager);

        Assert.Equal(FailureKind.Conflict, result.Kind);
        Assert.Equal(status, (await LoadAsync(id)).Status);
    }

    // ---- Review requires the claim ----------------------------------------------------------------------------------

    /// <summary>
    /// Another manager can't complete a review claimed by someone else, whatever the outcome; nothing changes. The claim
    /// owner then can, and the claim is cleared.
    /// </summary>
    [Theory]
    [InlineData(ReviewOutcome.Approve, null)]
    [InlineData(ReviewOutcome.Return, "Fix it.")]
    [InlineData(ReviewOutcome.Deny, "No.")]
    public async Task Only_the_claim_owner_can_complete_the_review(ReviewOutcome outcome, string? comment)
    {
        var id = await ClaimedAsync(_unitId, Alice);
        var input = new ReviewInput(outcome, comment, Today);

        var byOther = await _service.ReviewAsync(id, SecondManager, input);

        Assert.Equal(FailureKind.Conflict, byOther.Kind);
        Assert.Contains("Pat Manager", byOther.Error);
        var untouched = await LoadAsync(id);
        Assert.Equal((ApplicationStatus.UnderReview, Manager), (untouched.Status, untouched.ClaimedByUserId));
        Assert.Null(untouched.Lease);

        var byOwner = await _service.ReviewAsync(id, Manager, input);

        Assert.True(byOwner.Succeeded, byOwner.Error);
        var reviewed = await LoadAsync(id);
        Assert.Equal(outcome.ToStatus(), reviewed.Status);
        Assert.Null(reviewed.ClaimedByUserId);
        Assert.Null(reviewed.ClaimedAt);
    }

    /// <summary>A Submitted application can't be reviewed without claiming it first; the message says so.</summary>
    [Fact]
    public async Task A_review_cannot_be_completed_without_a_claim()
    {
        var id = await SubmittedAsync(_unitId, Alice);

        var result = await _service.ReviewAsync(id, Manager, new ReviewInput(ReviewOutcome.Approve, null, Today));

        Assert.Equal(FailureKind.Conflict, result.Kind);
        Assert.Contains("Claim", result.Error);
        Assert.Equal(ApplicationStatus.Submitted, (await LoadAsync(id)).Status);
        Assert.Equal(0, await CountLeasesAsync(_unitId));
    }

    /// <summary>A refused approval (lease conflict) keeps the application Under Review with the claim intact.</summary>
    [Fact]
    public async Task A_refused_approval_keeps_the_claim()
    {
        await ApprovedWithLeaseAsync(_unitId, Bob, start: Today.AddMonths(-3));
        var id = await ClaimedAsync(_unitId, Alice);

        var result = await _service.ReviewAsync(id, Manager, new ReviewInput(ReviewOutcome.Approve, null, Today));

        Assert.Equal(FailureKind.Conflict, result.Kind);
        Assert.Contains("under review", result.Error);
        Assert.Equal(Manager, (await LoadAsync(id)).ClaimedByUserId);
    }

    // ---- History ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The history lists every change oldest first, with who made it, the outcome and the comment: creation,
    /// submission, the claim, a release, a second claim by another manager and that manager's review.
    /// </summary>
    [Fact]
    public async Task Status_history_records_claims_releases_and_review_outcomes()
    {
        var id = await SubmittedAsync(_unitId, Alice);
        Assert.True((await _service.ClaimAsync(id, Manager)).Succeeded);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await _service.ReleaseAsync(id, Manager)).Succeeded);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await _service.ClaimAsync(id, SecondManager)).Succeeded);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await _service.ReviewAsync(id, SecondManager, new ReviewInput(ReviewOutcome.Return, "Fix the phone number.", null))).Succeeded);

        var history = await _service.GetStatusHistoryAsync(id);

        Assert.Collection(history,
            h => Assert.Equal((null, ApplicationStatus.Draft, "Alice Archer", (ReviewOutcome?)null), (h.FromStatus, h.ToStatus, h.ChangedByName, h.Outcome)),
            h => Assert.Equal((ApplicationStatus.Draft, ApplicationStatus.Submitted, "Alice Archer"), (h.FromStatus, h.ToStatus, h.ChangedByName)),
            h => Assert.Equal((ApplicationStatus.Submitted, ApplicationStatus.UnderReview, "Pat Manager", (ReviewOutcome?)null), (h.FromStatus, h.ToStatus, h.ChangedByName, h.Outcome)),
            h => Assert.Equal((ApplicationStatus.UnderReview, ApplicationStatus.Submitted, "Pat Manager"), (h.FromStatus, h.ToStatus, h.ChangedByName)),
            h => Assert.Equal((ApplicationStatus.Submitted, ApplicationStatus.UnderReview, "Sam Second"), (h.FromStatus, h.ToStatus, h.ChangedByName)),
            h =>
            {
                Assert.Equal((ApplicationStatus.UnderReview, ApplicationStatus.Returned, ReviewOutcome.Return), (h.FromStatus, h.ToStatus, h.Outcome));
                Assert.Equal(("Sam Second", "pm2@test", "Fix the phone number."), (h.ChangedByName, h.ChangedByEmail, h.Comment));
                Assert.Equal(_clock.GetUtcNow(), h.ChangedAt);
            });
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>A context on this test's database (in-memory, or SQLite after <see cref="UseSqliteAsync"/>), optionally with interceptors.</summary>
    private ApplicationDbContext NewContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>();
        if (_sqliteConnectionString is null)
        {
            options.UseInMemoryDatabase(_databaseName);
        }
        else
        {
            options.UseSqlite(_sqliteConnectionString);
        }

        return new ApplicationDbContext(options.AddInterceptors(interceptors).Options);
    }

    /// <summary>Creates a complete application for the unit and submits it (through the domain methods).</summary>
    /// <returns>The application id.</returns>
    private async Task<int> SubmittedAsync(int unitId, string applicantId)
    {
        await using var db = NewContext();
        var application = RentalApplication.StartDraft(unitId, applicantId, _clock.GetUtcNow());
        application.ApplicantInfoCompleted = true;
        application.ResidenceHistoryCompleted = true;
        application.TransitionTo(ApplicationStatus.Submitted, applicantId, _clock.GetUtcNow());
        db.RentalApplications.Add(application);
        await db.SaveChangesAsync();
        return application.Id;
    }

    /// <summary>A submitted application claimed by <paramref name="managerId"/> (default: <see cref="Manager"/>).</summary>
    /// <returns>The application id.</returns>
    private async Task<int> ClaimedAsync(int unitId, string applicantId, string managerId = Manager)
    {
        var id = await SubmittedAsync(unitId, applicantId);
        await using var db = NewContext();
        var application = await db.RentalApplications.SingleAsync(a => a.Id == id);
        application.Claim(managerId, _clock.GetUtcNow());
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>An application for the first unit, driven through legal steps to <paramref name="status"/>.</summary>
    /// <returns>The application id.</returns>
    private async Task<int> InStatusAsync(ApplicationStatus status)
    {
        await using var db = NewContext();
        var now = _clock.GetUtcNow();
        var application = RentalApplication.StartDraft(_unitId, Alice, now);
        if (status != ApplicationStatus.Draft && status != ApplicationStatus.Withdrawn)
        {
            application.TransitionTo(ApplicationStatus.Submitted, Alice, now);
        }

        if (status is ApplicationStatus.Returned or ApplicationStatus.Denied or ApplicationStatus.Approved)
        {
            application.Claim(Manager, now);
        }

        switch (status)
        {
            case ApplicationStatus.Returned:
            case ApplicationStatus.Denied:
                application.TransitionTo(status, Manager, now, status == ApplicationStatus.Returned ? ReviewOutcome.Return : ReviewOutcome.Deny, "Reason.");
                break;
            case ApplicationStatus.Approved:
                application.TransitionTo(status, Manager, now, ReviewOutcome.Approve);
                application.Lease = Lease.Issue(application, 1250m, Today.AddMonths(-14), now);
                break;
            case ApplicationStatus.Withdrawn:
                application.TransitionTo(status, Alice, now);
                break;
        }

        db.RentalApplications.Add(application);
        await db.SaveChangesAsync();
        return application.Id;
    }

    /// <summary>Puts a lease on a unit through another applicant's approved application.</summary>
    private async Task ApprovedWithLeaseAsync(int unitId, string applicantId, DateOnly start)
    {
        await using var db = NewContext();
        var now = _clock.GetUtcNow();
        var application = RentalApplication.StartDraft(unitId, applicantId, now);
        application.TransitionTo(ApplicationStatus.Submitted, applicantId, now);
        application.Claim(Manager, now);
        application.TransitionTo(ApplicationStatus.Approved, Manager, now, ReviewOutcome.Approve);
        application.Lease = Lease.Issue(application, 1250m, start, now);
        db.RentalApplications.Add(application);
        await db.SaveChangesAsync();
    }

    /// <summary>Reads an application with its history and lease from a fresh context.</summary>
    private async Task<RentalApplication> LoadAsync(int id)
    {
        await using var db = NewContext();
        return await db.RentalApplications.Include(a => a.StatusHistory).Include(a => a.Lease).AsNoTracking().SingleAsync(a => a.Id == id);
    }

    /// <summary>How many leases the unit has, read from a fresh context.</summary>
    private async Task<int> CountLeasesAsync(int unitId)
    {
        await using var db = NewContext();
        return await db.Leases.CountAsync(l => l.UnitId == unitId);
    }

    /// <summary>
    /// Runs an action once, just before the intercepted context saves, to interleave another user's complete
    /// operation between this operation's checks and its commit.
    /// </summary>
    private sealed class BeforeFirstSave(Func<Task> action) : SaveChangesInterceptor
    {
        private bool _done;

        /// <summary>Runs the action the first time the context is about to save.</summary>
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                await action();
            }

            return result;
        }
    }
}
