// =====================================================================================================================
// Module: RentalManagement.Tests / Services / ApplicationServiceTests.cs
// Purpose: Tests of the applicant workflow in ApplicationService against an EF Core in-memory database and a fake
//          clock: starting/resuming drafts, section validation and saving, residences, ownership checks, submit
//          rules (both sections, re-validation, active lease), return → correct → resubmit, withdrawal, terminal
//          statuses and the application list. The manager's review is simulated through the domain method.
// =====================================================================================================================

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using RentalManagement.Core.Data;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services;
using RentalManagement.Core.Services.Applications;

namespace RentalManagement.Tests.Services;

/// <summary>
/// Tests of <see cref="ApplicationService"/>. Each test gets a fresh database with two units and three users:
/// applicants Alice and Bob, and a property manager.
/// </summary>
public sealed class ApplicationServiceTests : IDisposable
{
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Manager = "manager";
    private static readonly DateOnly Today = new(2026, 10, 8);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly ApplicationDbContext _db;
    private readonly ApplicationService _service;
    private readonly Unit _unit;
    private readonly Unit _otherUnit;

    /// <summary>Creates the per-test database and seeds the property, its two units and the three users.</summary>
    public ApplicationServiceTests()
    {
        _clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _service = new ApplicationService(_db, _clock);

        var type = new UnitType { Name = "Apartment", IsActive = true };
        var property = new Property
        {
            Name = "Maple Court",
            Address = new Address { Street = "1 Maple St", City = "Troy", State = "NY", PostalCode = "12180" },
        };
        _unit = new Unit { UnitNumber = "101", Bedrooms = 1, MonthlyRent = 1200m, UnitType = type };
        _otherUnit = new Unit { UnitNumber = "102", Bedrooms = 2, MonthlyRent = 1500m, UnitType = type };
        property.Units.Add(_unit);
        property.Units.Add(_otherUnit);
        _db.Properties.Add(property);
        _db.Users.AddRange(
            new ApplicationUser { Id = Alice, UserName = "alice@test", Email = "alice@test", FirstName = "Alice", LastName = "Archer", PhoneNumber = "(518) 555-0101" },
            new ApplicationUser { Id = Bob, UserName = "bob@test", Email = "bob@test", FirstName = "Bob", LastName = "Baker" },
            new ApplicationUser { Id = Manager, UserName = "pm@test", Email = "pm@test", FirstName = "Pat", LastName = "Manager" });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    /// <summary>Disposes the test's DbContext.</summary>
    public void Dispose() => _db.Dispose();

    // ---- Start ------------------------------------------------------------------------------------------------

    /// <summary>Starting creates a Draft pre-filled from the profile (not yet marked saved) with a creation history entry.</summary>
    [Fact]
    public async Task Start_creates_a_prefilled_draft_with_a_creation_history_entry()
    {
        var result = await _service.StartAsync(_unit.Id, Alice);

        Assert.True(result.Succeeded, result.Error);
        var application = await LoadAsync(result.Value);
        Assert.Equal(ApplicationStatus.Draft, application.Status);
        Assert.Equal(("Alice", "Archer", "alice@test", "(518) 555-0101"),
            (application.FirstName, application.LastName, application.Email, application.Phone));
        Assert.False(application.ApplicantInfoCompleted); // pre-filled is not the same as saved
        var created = Assert.Single(application.StatusHistory);
        Assert.Equal((null, ApplicationStatus.Draft, Alice), (created.FromStatus, created.ToStatus, created.ChangedById));
    }

    /// <summary>Starting again reopens the applicant's open application for that unit; another applicant gets their own.</summary>
    [Fact]
    public async Task Start_resumes_the_applicants_open_application_for_the_same_unit()
    {
        var first = await _service.StartAsync(_unit.Id, Alice);
        var second = await _service.StartAsync(_unit.Id, Alice);
        var bobs = await _service.StartAsync(_unit.Id, Bob);

        Assert.Equal(first.Value, second.Value);
        Assert.NotEqual(first.Value, bobs.Value);
    }

    /// <summary>A withdrawn application isn't reopened; starting again creates a new draft.</summary>
    [Fact]
    public async Task Start_after_withdrawing_creates_a_new_draft()
    {
        var first = await _service.StartAsync(_unit.Id, Alice);
        await _service.WithdrawAsync(first.Value, Alice);

        var second = await _service.StartAsync(_unit.Id, Alice);

        Assert.NotEqual(first.Value, second.Value);
    }

    /// <summary>No draft can be started for a unit whose lease covers today.</summary>
    [Fact]
    public async Task Start_is_refused_for_a_leased_unit()
    {
        await AddActiveLeaseAsync(_unit.Id);

        var result = await _service.StartAsync(_unit.Id, Alice);

        Assert.False(result.Succeeded);
        Assert.Equal(FailureKind.Conflict, result.Kind);
    }

    // ---- Sections ---------------------------------------------------------------------------------------------

    /// <summary>Invalid Section 1 data is not saved at all, and every invalid field is reported.</summary>
    [Fact]
    public async Task Invalid_applicant_info_is_not_saved_and_reports_every_field()
    {
        var id = (await _service.StartAsync(_unit.Id, Alice)).Value;

        var result = await _service.SaveApplicantInfoAsync(id, Alice, ApplicationRulesTests.ValidInfo with { Phone = "123", City = "" });

        Assert.False(result.Succeeded);
        Assert.Equal(FailureKind.Validation, result.Kind);
        Assert.Equal(["Phone", "City"], result.Errors.Select(e => e.Field));
        var application = await LoadAsync(id);
        Assert.False(application.ApplicantInfoCompleted);
        Assert.Null(application.CurrentAddress);
    }

    /// <summary>Valid Section 1 data is saved trimmed and marks the section complete.</summary>
    [Fact]
    public async Task Valid_applicant_info_is_saved_trimmed_and_marks_the_section_complete()
    {
        var id = (await _service.StartAsync(_unit.Id, Alice)).Value;

        var result = await _service.SaveApplicantInfoAsync(id, Alice, ApplicationRulesTests.ValidInfo with { FirstName = "  Ada " });

        Assert.True(result.Succeeded, result.Error);
        var application = await LoadAsync(id);
        Assert.True(application.ApplicantInfoCompleted);
        Assert.Equal("Ada", application.FirstName);
        Assert.Equal("Troy", application.CurrentAddress!.City);
    }

    /// <summary>Another applicant gets NotFound for every command and can't load the application; a manager can view it.</summary>
    [Fact]
    public async Task Another_applicant_cannot_change_the_application()
    {
        var id = (await _service.StartAsync(_unit.Id, Alice)).Value;

        var save = await _service.SaveApplicantInfoAsync(id, Bob, ApplicationRulesTests.ValidInfo);
        var add = await _service.AddResidenceAsync(id, Bob, ApplicationRulesTests.ValidResidence);
        var submit = await _service.SubmitAsync(id, Bob);
        var withdraw = await _service.WithdrawAsync(id, Bob);

        Assert.All(new OperationResult[] { save, add, submit, withdraw }, r => Assert.Equal(FailureKind.NotFound, r.Kind));
        Assert.Null(await _service.GetAsync(id, Bob, isManager: false));
        Assert.NotNull(await _service.GetAsync(id, Manager, isManager: true));
    }

    /// <summary>A residence with move-out before move-in is rejected on MoveOutDate and not saved.</summary>
    [Fact]
    public async Task Residence_with_bad_dates_is_rejected()
    {
        var id = (await _service.StartAsync(_unit.Id, Alice)).Value;

        var result = await _service.AddResidenceAsync(id, Alice,
            ApplicationRulesTests.ValidResidence with { MoveInDate = new DateOnly(2025, 1, 1), MoveOutDate = new DateOnly(2024, 1, 1) });

        Assert.False(result.Succeeded);
        Assert.Equal("MoveOutDate", result.Field);
        Assert.Empty(await _service.GetResidencesAsync(id));
    }

    /// <summary>A residence can be added, edited and removed by its owner, and is invisible to another applicant.</summary>
    [Fact]
    public async Task Residences_are_added_edited_and_removed()
    {
        var id = (await _service.StartAsync(_unit.Id, Alice)).Value;

        var added = await _service.AddResidenceAsync(id, Alice, ApplicationRulesTests.ValidResidence);
        var edited = await _service.UpdateResidenceAsync(added.Value, Alice, ApplicationRulesTests.ValidResidence with { LandlordName = "New Landlord" });

        Assert.True(edited.Succeeded, edited.Error);
        Assert.Equal("New Landlord", (await _service.GetResidenceAsync(added.Value, Alice))!.Input.LandlordName);
        Assert.Null(await _service.GetResidenceAsync(added.Value, Bob));

        var removed = await _service.RemoveResidenceAsync(added.Value, Alice);
        Assert.True(removed.Succeeded, removed.Error);
        Assert.Empty(await _service.GetResidencesAsync(id));
    }

    /// <summary>Section 2 can't be completed without a residence; the message is section-level.</summary>
    [Fact]
    public async Task Residence_history_cannot_be_completed_without_a_residence()
    {
        var id = (await _service.StartAsync(_unit.Id, Alice)).Value;

        var result = await _service.CompleteResidenceHistoryAsync(id, Alice);

        Assert.False(result.Succeeded);
        Assert.Null(result.Field); // a section-level message
        Assert.False((await LoadAsync(id)).ResidenceHistoryCompleted);
    }

    /// <summary>Removing the only residence clears Section 2's completed flag.</summary>
    [Fact]
    public async Task Removing_the_last_residence_makes_the_section_incomplete_again()
    {
        var id = (await _service.StartAsync(_unit.Id, Alice)).Value;
        var residence = await _service.AddResidenceAsync(id, Alice, ApplicationRulesTests.ValidResidence);
        await _service.CompleteResidenceHistoryAsync(id, Alice);
        Assert.True((await LoadAsync(id)).ResidenceHistoryCompleted);

        await _service.RemoveResidenceAsync(residence.Value, Alice);

        Assert.False((await LoadAsync(id)).ResidenceHistoryCompleted);
    }

    // ---- Submit -----------------------------------------------------------------------------------------------

    /// <summary>Submit is refused while Residence History is unsaved, and the blocker is listed for the Summary.</summary>
    [Fact]
    public async Task Submit_requires_both_sections_to_be_saved()
    {
        var id = (await _service.StartAsync(_unit.Id, Alice)).Value;
        await _service.SaveApplicantInfoAsync(id, Alice, ApplicationRulesTests.ValidInfo);

        var result = await _service.SubmitAsync(id, Alice);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Message.Contains("Residence History"));
        Assert.Equal(ApplicationStatus.Draft, (await LoadAsync(id)).Status);
        Assert.Single(await _service.GetSubmitBlockersAsync(id, Alice));
    }

    /// <summary>A complete draft is submitted with SubmittedAt and history, after which section changes are refused.</summary>
    [Fact]
    public async Task Complete_application_is_submitted_with_history_and_becomes_read_only()
    {
        var id = await CompleteDraftAsync(_unit.Id, Alice);
        Assert.Empty(await _service.GetSubmitBlockersAsync(id, Alice));

        var result = await _service.SubmitAsync(id, Alice);

        Assert.True(result.Succeeded, result.Error);
        var application = await LoadAsync(id);
        Assert.Equal(ApplicationStatus.Submitted, application.Status);
        Assert.Equal(_clock.GetUtcNow(), application.SubmittedAt);
        Assert.Contains(application.StatusHistory, h => h.FromStatus == ApplicationStatus.Draft && h.ToStatus == ApplicationStatus.Submitted && h.ChangedById == Alice);

        var details = (await _service.GetAsync(id, Alice, isManager: false))!;
        Assert.False(details.IsEditableBy(Alice));
        var edit = await _service.SaveApplicantInfoAsync(id, Alice, ApplicationRulesTests.ValidInfo);
        var add = await _service.AddResidenceAsync(id, Alice, ApplicationRulesTests.ValidResidence);
        Assert.Equal(FailureKind.Conflict, edit.Kind);
        Assert.Equal(FailureKind.Conflict, add.Kind);
    }

    /// <summary>Submit is refused once the unit has an active lease, and other open applications for the unit are left alone.</summary>
    [Fact]
    public async Task Submit_is_rejected_when_the_unit_has_an_active_lease_and_other_applications_are_untouched()
    {
        var alices = await CompleteDraftAsync(_unit.Id, Alice);
        var bobs = await CompleteDraftAsync(_unit.Id, Bob);
        Assert.True((await _service.SubmitAsync(bobs, Bob)).Succeeded);
        await AddActiveLeaseAsync(_unit.Id);

        var result = await _service.SubmitAsync(alices, Alice);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Message.Contains("active lease"));
        Assert.Equal(ApplicationStatus.Draft, (await LoadAsync(alices)).Status);
        Assert.Equal(ApplicationStatus.Submitted, (await LoadAsync(bobs)).Status);
    }

    /// <summary>Only a lease covering today blocks submission; an expired one doesn't.</summary>
    [Fact]
    public async Task Expired_lease_does_not_block_submission()
    {
        var id = await CompleteDraftAsync(_unit.Id, Alice);
        await AddLeaseAsync(_unit.Id, start: Today.AddMonths(-14));

        var result = await _service.SubmitAsync(id, Alice);

        Assert.True(result.Succeeded, result.Error);
    }

    /// <summary>Submit re-validates saved data, so data that is no longer valid can't be submitted.</summary>
    [Fact]
    public async Task Submit_rechecks_saved_data_against_the_rules()
    {
        var id = await CompleteDraftAsync(_unit.Id, Alice);
        // Data that was saved in a valid state but is invalid now (e.g. written by an older version).
        var application = await _db.RentalApplications.SingleAsync(a => a.Id == id);
        application.Phone = "bad";
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _service.SubmitAsync(id, Alice);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Message.Contains("Applicant Information"));
    }

    // ---- Return, correct, resubmit ----------------------------------------------------------------------------

    /// <summary>A returned application shows the reviewer's comment, can be corrected and resubmitted, and its history records each step.</summary>
    [Fact]
    public async Task Returned_application_can_be_corrected_and_resubmitted()
    {
        var id = await CompleteDraftAsync(_unit.Id, Alice);
        await _service.SubmitAsync(id, Alice);
        await ReturnAsync(id, "Please fix your phone number.");

        var returned = (await _service.GetAsync(id, Alice, isManager: false))!;
        Assert.True(returned.IsEditableBy(Alice));
        Assert.Equal("Please fix your phone number.", returned.LatestReview!.Comment);

        var corrected = await _service.SaveApplicantInfoAsync(id, Alice, ApplicationRulesTests.ValidInfo with { Phone = "(518) 555-9999" });
        var resubmitted = await _service.SubmitAsync(id, Alice);

        Assert.True(corrected.Succeeded, corrected.Error);
        Assert.True(resubmitted.Succeeded, resubmitted.Error);
        var history = (await LoadAsync(id)).StatusHistory.OrderBy(h => h.Id).Select(h => h.ToStatus);
        Assert.Equal([ApplicationStatus.Draft, ApplicationStatus.Submitted, ApplicationStatus.UnderReview, ApplicationStatus.Returned, ApplicationStatus.Submitted], history);
    }

    // ---- Withdraw ---------------------------------------------------------------------------------------------

    /// <summary>Draft, Submitted and Returned applications can be withdrawn, and the history records the status it left.</summary>
    [Theory]
    [InlineData(ApplicationStatus.Draft)]
    [InlineData(ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.Returned)]
    public async Task Open_applications_can_be_withdrawn(ApplicationStatus status)
    {
        var id = await ApplicationInStatusAsync(status);

        var result = await _service.WithdrawAsync(id, Alice);

        Assert.True(result.Succeeded, result.Error);
        var application = await LoadAsync(id);
        Assert.Equal(ApplicationStatus.Withdrawn, application.Status);
        Assert.Equal(status, application.StatusHistory.OrderBy(h => h.Id).Last().FromStatus);
    }

    /// <summary>Withdrawn and Denied applications refuse withdraw, edit and submit, and keep their status.</summary>
    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Denied)]
    public async Task Terminal_applications_cannot_be_withdrawn_or_edited(ApplicationStatus status)
    {
        var id = await ApplicationInStatusAsync(status);

        var withdraw = await _service.WithdrawAsync(id, Alice);
        var edit = await _service.SaveApplicantInfoAsync(id, Alice, ApplicationRulesTests.ValidInfo);
        var submit = await _service.SubmitAsync(id, Alice);

        Assert.Equal(FailureKind.Conflict, withdraw.Kind);
        Assert.Equal(FailureKind.Conflict, edit.Kind);
        Assert.Equal(FailureKind.Conflict, submit.Kind);
        Assert.Equal(status, (await LoadAsync(id)).Status);
    }

    /// <summary>The list shows an applicant only their applications and a manager all of them.</summary>
    [Fact]
    public async Task Lists_show_applicants_their_own_and_managers_everything()
    {
        await _service.StartAsync(_unit.Id, Alice);
        await _service.StartAsync(_otherUnit.Id, Bob);

        Assert.Equal(1, (await _service.ListPageAsync(Alice, isManager: false, ApplicationListQuery.Default)).TotalCount);
        Assert.Equal(2, (await _service.ListPageAsync(Manager, isManager: true, ApplicationListQuery.Default)).TotalCount);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    /// <summary>Reads an application and its history fresh from the database (not from the change tracker).</summary>
    private async Task<RentalApplication> LoadAsync(int id)
    {
        _db.ChangeTracker.Clear();
        return await _db.RentalApplications.Include(a => a.StatusHistory).AsNoTracking().SingleAsync(a => a.Id == id);
    }

    /// <summary>Starts a draft and saves both sections through the service, asserting each step succeeds.</summary>
    /// <returns>The application id, ready to submit.</returns>
    private async Task<int> CompleteDraftAsync(int unitId, string applicantId)
    {
        var id = (await _service.StartAsync(unitId, applicantId)).Value;
        Assert.True((await _service.SaveApplicantInfoAsync(id, applicantId, ApplicationRulesTests.ValidInfo)).Succeeded);
        Assert.True((await _service.AddResidenceAsync(id, applicantId, ApplicationRulesTests.ValidResidence)).Succeeded);
        Assert.True((await _service.CompleteResidenceHistoryAsync(id, applicantId)).Succeeded);
        return id;
    }

    /// <summary>Creates Alice's application and drives it through legal steps to <paramref name="status"/>.</summary>
    /// <returns>The application id.</returns>
    private async Task<int> ApplicationInStatusAsync(ApplicationStatus status)
    {
        var id = await CompleteDraftAsync(_unit.Id, Alice);
        switch (status)
        {
            case ApplicationStatus.Submitted:
                await _service.SubmitAsync(id, Alice);
                break;
            case ApplicationStatus.Returned:
                await _service.SubmitAsync(id, Alice);
                await ReturnAsync(id, "Fix it.");
                break;
            case ApplicationStatus.Withdrawn:
                await _service.WithdrawAsync(id, Alice);
                break;
            case ApplicationStatus.Denied:
                await _service.SubmitAsync(id, Alice);
                await ReviewAsync(id, ReviewOutcome.Deny, "No.");
                break;
        }

        Assert.Equal(status, (await LoadAsync(id)).Status);
        return id;
    }

    /// <summary>Simulates the manager's review (the review workflow is a later phase).</summary>
    private Task ReturnAsync(int id, string comment) => ReviewAsync(id, ReviewOutcome.Return, comment);

    /// <summary>Records a manager's review outcome directly through the domain's transition method.</summary>
    private async Task ReviewAsync(int id, ReviewOutcome outcome, string comment)
    {
        var application = await _db.RentalApplications.SingleAsync(a => a.Id == id);
        application.Claim(Manager, _clock.GetUtcNow());
        application.TransitionTo(outcome.ToStatus(), Manager, _clock.GetUtcNow(), outcome, comment);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    /// <summary>Puts a lease that started a month ago (so it covers today) on a unit.</summary>
    private Task AddActiveLeaseAsync(int unitId) => AddLeaseAsync(unitId, Today.AddMonths(-1));

    /// <summary>Puts a twelve-month lease starting on <paramref name="start"/> on a unit, via another approved application.</summary>
    private async Task AddLeaseAsync(int unitId, DateOnly start)
    {
        var application = RentalApplication.StartDraft(unitId, Manager, _clock.GetUtcNow());
        application.TransitionTo(ApplicationStatus.Submitted, Manager, _clock.GetUtcNow());
        application.Claim(Manager, _clock.GetUtcNow());
        application.TransitionTo(ApplicationStatus.Approved, Manager, _clock.GetUtcNow(), ReviewOutcome.Approve);
        application.Lease = Lease.Issue(application, 1200m, start, _clock.GetUtcNow());
        _db.RentalApplications.Add(application);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }
}
