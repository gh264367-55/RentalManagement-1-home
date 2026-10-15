// =====================================================================================================================
// Module: RentalManagement.Core / Services / Applications / ApplicationService.cs
// Purpose: Business logic of the applicant's rental application workflow: start or resume a draft, save Section 1,
//          add/edit/remove residences and complete Section 2, submit or resubmit, and withdraw. Also provides the
//          read models for the application page, its residences, submit blockers and the paged, sorted application
//          list (filtered, counted, sorted and paged in SQL). Every command re-checks ownership, editable status,
//          validity and unit availability on the server. The property manager's side (review, lease, status history)
//          is in ApplicationReviewService.
// =====================================================================================================================

using Microsoft.EntityFrameworkCore;
using RentalManagement.Core.Data;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services.Properties;

namespace RentalManagement.Core.Services.Applications;

/// <summary>
/// The applicant side of the rental application workflow: start a draft, save each section, add/edit/remove
/// residences, submit, withdraw, and correct and resubmit a returned application.
/// <para>
/// Every command re-checks on the server, whatever the browser showed:
/// <list type="bullet">
///   <item><b>ownership</b>: only the applicant who owns the application may change it (others get NotFound, so
///   ids of other people's applications are not disclosed);</item>
///   <item><b>status</b>: sections change only in Draft or Returned; status changes go through
///   <see cref="RentalApplication.TransitionTo"/> and therefore the state machine;</item>
///   <item><b>validity</b>: <see cref="ApplicationRules"/> on every save and again at submit;</item>
///   <item><b>availability</b>: no draft for, and no submission of, a unit whose lease covers today.</item>
/// </list>
/// Every change rotates the application's concurrency stamp, so two racing changes (e.g. a withdrawal and a review)
/// cannot both commit.
/// </para>
/// </summary>
public sealed class ApplicationService(ApplicationDbContext db, TimeProvider clock)
{
    private const string NotFoundMessage = "Application not found.";
    private const string StaleMessage = "This application was changed by someone else at the same time. Reload the page and try again.";

    /// <summary>The single rule for whether an applicant may change sections: they own it and it is Draft or Returned.</summary>
    /// <param name="status">The application's current status.</param>
    /// <param name="isOwner">Whether the signed-in user is the application's applicant.</param>
    /// <returns>True when the sections may be changed.</returns>
    public static bool CanApplicantEdit(ApplicationStatus status, bool isOwner) =>
        isOwner && ApplicationStateMachine.IsEditableByApplicant(status);

    // ---- Queries ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Loads an application for display. Applicants see only their own; property managers see every application.
    /// Returns null when the application doesn't exist or isn't visible to the user.
    /// </summary>
    /// <param name="id">The application id.</param>
    /// <param name="userId">The signed-in user's id.</param>
    /// <param name="isManager">Whether the user is a property manager (sees every application).</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The details, including the lease, the latest review and whether the unit is leased today.</returns>
    public async Task<ApplicationDetails?> GetAsync(int id, string userId, bool isManager, CancellationToken cancellationToken = default)
    {
        var today = clock.GetToday();
        var details = await db.RentalApplications
            .AsNoTracking()
            .Where(a => a.Id == id && (isManager || a.ApplicantId == userId))
            .Select(a => new ApplicationDetails(
                a.Id,
                a.Status,
                a.ApplicantId,
                a.Applicant.FirstName + " " + a.Applicant.LastName,
                a.UnitId,
                a.Unit.UnitNumber,
                a.Unit.Property.Name,
                a.Unit.Property.Address.Street + ", " + a.Unit.Property.Address.City + ", " + a.Unit.Property.Address.State + " " + a.Unit.Property.Address.PostalCode,
                a.Unit.MonthlyRent,
                new ApplicantInfoInput(
                    a.FirstName, a.LastName, a.Phone, a.Email,
                    a.CurrentAddress == null ? null : a.CurrentAddress.Street,
                    a.CurrentAddress == null ? null : a.CurrentAddress.City,
                    a.CurrentAddress == null ? null : a.CurrentAddress.State,
                    a.CurrentAddress == null ? null : a.CurrentAddress.PostalCode),
                a.ApplicantInfoCompleted,
                a.ResidenceHistoryCompleted,
                a.Residences.Count,
                a.CreatedAt,
                a.UpdatedAt,
                a.SubmittedAt,
                a.Lease == null ? null : new LeaseSummary(a.Lease.StartDate, a.Lease.EndDate, a.Lease.MonthlyRent),
                a.StatusHistory
                    .Where(h => h.Outcome != null)
                    .OrderByDescending(h => h.ChangedAt)
                    .Select(h => new ReviewNote(h.Outcome!.Value, h.Comment, h.ChangedBy.FirstName + " " + h.ChangedBy.LastName, h.ChangedAt))
                    .FirstOrDefault(),
                a.Unit.Leases.Any(l => l.StartDate <= today && l.EndDate >= today),
                // The claim is internal to the review team: applicants never get the manager's identity.
                isManager ? a.ClaimedByUserId : null,
                isManager && a.ClaimedBy != null ? a.ClaimedBy.FirstName + " " + a.ClaimedBy.LastName : null,
                isManager ? a.ClaimedAt : null))
            .SingleOrDefaultAsync(cancellationToken);

        return details;
    }

    /// <summary>
    /// Residences of an application, oldest move-in last. Callers must have checked access with <see cref="GetAsync"/>
    /// (the ResidenceList view component is only invoked for an application the page already loaded).
    /// </summary>
    public async Task<IReadOnlyList<ResidenceDetails>> GetResidencesAsync(int applicationId, CancellationToken cancellationToken = default) =>
        await db.Residences
            .AsNoTracking()
            .Where(r => r.RentalApplicationId == applicationId)
            .OrderByDescending(r => r.MoveInDate)
            .Select(r => new ResidenceDetails(
                r.Id,
                r.RentalApplicationId,
                new ResidenceInput(
                    r.Address.Street, r.Address.City, r.Address.State, r.Address.PostalCode,
                    r.LandlordName, r.LandlordPhone, r.MoveInDate, r.MoveOutDate)))
            .ToListAsync(cancellationToken);

    /// <summary>A residence the applicant owns, or null.</summary>
    public async Task<ResidenceDetails?> GetResidenceAsync(int residenceId, string applicantId, CancellationToken cancellationToken = default) =>
        await db.Residences
            .AsNoTracking()
            .Where(r => r.Id == residenceId && r.RentalApplication.ApplicantId == applicantId)
            .Select(r => new ResidenceDetails(
                r.Id,
                r.RentalApplicationId,
                new ResidenceInput(
                    r.Address.Street, r.Address.City, r.Address.State, r.Address.PostalCode,
                    r.LandlordName, r.LandlordPhone, r.MoveInDate, r.MoveOutDate)))
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>What still prevents submission, as messages for the Summary. Empty means Submit is allowed.</summary>
    public async Task<IReadOnlyList<string>> GetSubmitBlockersAsync(int id, string applicantId, CancellationToken cancellationToken = default)
    {
        var application = await db.RentalApplications
            .AsNoTracking()
            .Include(a => a.Residences)
            .SingleOrDefaultAsync(a => a.Id == id && a.ApplicantId == applicantId, cancellationToken);
        return application is null ? [NotFoundMessage] : await FindSubmitBlockersAsync(application, cancellationToken);
    }

    /// <summary>
    /// One page of the application list: applicants see only their own applications, property managers see all of
    /// them, optionally narrowed by status and property, sorted by a whitelisted column. Filtering, counting, sorting
    /// and paging are all composed on the <see cref="IQueryable"/> and run in SQL (a COUNT, then one page with
    /// OFFSET/FETCH); nothing is sorted or paged in memory.
    /// </summary>
    /// <param name="userId">The signed-in user's id.</param>
    /// <param name="isManager">Whether the user is a property manager.</param>
    /// <param name="query">The validated page, sort and filter request (see <see cref="ApplicationListQuery.Create"/>).</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>The page's rows and the filtered total. A page beyond the last one has no rows.</returns>
    public async Task<PagedResult<ApplicationListItem>> ListPageAsync(
        string userId, bool isManager, ApplicationListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var totalCount = await FilterList(userId, isManager, query.Filter).CountAsync(cancellationToken);

        // Past the end: no need to ask for rows (and Skip is then guaranteed to fit in an int below).
        if (query.Skip >= totalCount)
        {
            return new PagedResult<ApplicationListItem>([], query.Page, query.PageSize, totalCount);
        }

        var items = await QueryPage(userId, isManager, query).ToListAsync(cancellationToken);
        return new PagedResult<ApplicationListItem>(items, query.Page, query.PageSize, totalCount);
    }

    /// <summary>
    /// Builds the query for one page without running it: filter, sort, project, then Skip/Take (one SELECT with
    /// ORDER BY … OFFSET … FETCH on SQL Server). Internal so tests can inspect the generated SQL. Callers must ensure
    /// <see cref="ApplicationListQuery.Skip"/> is below the filtered count (as <see cref="ListPageAsync"/> does).
    /// </summary>
    /// <param name="userId">The signed-in user's id.</param>
    /// <param name="isManager">Whether the user is a property manager.</param>
    /// <param name="query">The page, sort and filter request.</param>
    /// <returns>The composed, not yet executed, query.</returns>
    internal IQueryable<ApplicationListItem> QueryPage(string userId, bool isManager, ApplicationListQuery query) =>
        Page(FilterList(userId, isManager, query.Filter), query);

    /// <summary>
    /// Sorts, projects and pages already-filtered applications (one SELECT with ORDER BY … OFFSET … FETCH). Shared by
    /// the application list and the review queue so both sort and page the same way.
    /// </summary>
    /// <param name="filtered">The filtered applications.</param>
    /// <param name="query">The page and sort request.</param>
    /// <returns>The composed, not yet executed, query.</returns>
    internal static IQueryable<ApplicationListItem> Page(IQueryable<RentalApplication> filtered, ApplicationListQuery query) =>
        Sort(filtered, query.SortBy, query.SortDirection)
            .Select(a => new ApplicationListItem(
                a.Id,
                a.Status,
                a.Applicant.FirstName + " " + a.Applicant.LastName,
                a.Unit.Property.Name,
                a.Unit.UnitNumber,
                a.UpdatedAt,
                a.SubmittedAt,
                a.ClaimedBy == null ? null : a.ClaimedBy.FirstName + " " + a.ClaimedBy.LastName,
                a.ClaimedAt))
            .Skip((int)Math.Min(query.Skip, int.MaxValue))
            .Take(query.PageSize);

    /// <summary>
    /// The list's filter conditions. The ownership condition is added first and unconditionally for applicants, so no
    /// filter value can widen what they see.
    /// </summary>
    /// <param name="userId">The signed-in user's id.</param>
    /// <param name="isManager">Whether the user is a property manager.</param>
    /// <param name="filter">The status and property criteria.</param>
    /// <returns>The filtered, untracked applications.</returns>
    private IQueryable<RentalApplication> FilterList(string userId, bool isManager, ApplicationListFilter filter)
    {
        var query = db.RentalApplications.AsNoTracking();

        if (!isManager)
        {
            query = query.Where(a => a.ApplicantId == userId);
        }

        return ApplyFilter(query, filter);
    }

    /// <summary>Narrows applications by the optional status and property criteria (never widens).</summary>
    /// <param name="query">The applications the caller may see.</param>
    /// <param name="filter">The status and property criteria.</param>
    /// <returns>The narrowed query.</returns>
    internal static IQueryable<RentalApplication> ApplyFilter(IQueryable<RentalApplication> query, ApplicationListFilter filter)
    {
        if (filter.Status is { } status)
        {
            query = query.Where(a => a.Status == status);
        }

        if (filter.PropertyId is { } propertyId)
        {
            query = query.Where(a => a.Unit.PropertyId == propertyId);
        }

        return query;
    }

    /// <summary>
    /// Applies a whitelisted sort. Every column ends with the application id in the same direction, so the order is
    /// total and paging is stable (no row appears on two pages or on none). Text columns sort by the database
    /// collation; Status sorts by its stored name. Submitted date always puts never-submitted drafts last, then
    /// breaks ties on the last update; claimed date likewise puts unclaimed applications last.
    /// </summary>
    /// <param name="query">The filtered applications.</param>
    /// <param name="column">The sort column.</param>
    /// <param name="direction">The sort direction.</param>
    /// <returns>The ordered query.</returns>
    internal static IOrderedQueryable<RentalApplication> Sort(
        IQueryable<RentalApplication> query, ApplicationSortColumn column, SortDirection direction)
    {
        var descending = direction == SortDirection.Descending;
        var ordered = column switch
        {
            ApplicationSortColumn.Id => query.OrderBy(a => a.Id, descending),
            ApplicationSortColumn.ApplicantName => query
                .OrderBy(a => a.Applicant.LastName, descending)
                .ThenBy(a => a.Applicant.FirstName, descending),
            ApplicationSortColumn.PropertyName => query
                .OrderBy(a => a.Unit.Property.Name, descending)
                .ThenBy(a => a.Unit.UnitNumber, descending),
            ApplicationSortColumn.UnitNumber => query
                .OrderBy(a => a.Unit.UnitNumber, descending)
                .ThenBy(a => a.Unit.Property.Name, descending),
            ApplicationSortColumn.Status => query.OrderBy(a => a.Status, descending),
            ApplicationSortColumn.SubmittedDate => query
                .OrderBy(a => a.SubmittedAt == null)
                .ThenBy(a => a.SubmittedAt, descending)
                .ThenBy(a => a.UpdatedAt, descending),
            ApplicationSortColumn.UpdatedDate => query.OrderBy(a => a.UpdatedAt, descending),
            ApplicationSortColumn.ClaimedDate => query
                .OrderBy(a => a.ClaimedAt == null)
                .ThenBy(a => a.ClaimedAt, descending),
            _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Not a sortable column."),
        };

        return column == ApplicationSortColumn.Id ? ordered : ordered.ThenBy(a => a.Id, descending);
    }

    /// <summary>
    /// The properties offered in the list's property filter: every property for a manager, and for an applicant only
    /// the properties they have applied to (so the dropdown matches what their list can contain).
    /// </summary>
    /// <param name="userId">The signed-in user's id.</param>
    /// <param name="isManager">Whether the user is a property manager.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The dropdown options ordered by name.</returns>
    public async Task<IReadOnlyList<PropertyOption>> GetListPropertyOptionsAsync(
        string userId, bool isManager, CancellationToken cancellationToken = default)
    {
        var properties = db.Properties.AsNoTracking();
        if (!isManager)
        {
            properties = properties.Where(p => p.Units.Any(u => u.Applications.Any(a => a.ApplicantId == userId)));
        }

        return await properties
            .OrderBy(p => p.Name)
            .Select(p => new PropertyOption(p.Id, p.Name))
            .ToListAsync(cancellationToken);
    }

    // ---- Commands ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts a draft for an available unit, or returns the applicant's open application (Draft, Submitted, Under Review or
    /// Returned) for that unit so they don't end up with duplicates. The applicant's name, email and phone are
    /// pre-filled from their profile; the section still has to be saved to count as complete.
    /// </summary>
    public async Task<OperationResult<int>> StartAsync(int unitId, string applicantId, CancellationToken cancellationToken = default)
    {
        var openId = await db.RentalApplications
            .Where(a => a.UnitId == unitId && a.ApplicantId == applicantId
                        && a.Status != ApplicationStatus.Approved && a.Status != ApplicationStatus.Denied && a.Status != ApplicationStatus.Withdrawn)
            .Select(a => (int?)a.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (openId is { } existing)
        {
            return OperationResult<int>.Success(existing);
        }

        if (!await db.Units.AnyAsync(u => u.Id == unitId, cancellationToken))
        {
            return OperationResult<int>.From(OperationResult.NotFound("That unit no longer exists."));
        }

        if (await UnitHasActiveLeaseAsync(unitId, cancellationToken))
        {
            return OperationResult<int>.From(OperationResult.Conflict("That unit is leased and not available for applications."));
        }

        var applicant = await db.Users.AsNoTracking().SingleAsync(u => u.Id == applicantId, cancellationToken);
        var application = RentalApplication.StartDraft(unitId, applicantId, clock.GetUtcNow());
        application.FirstName = applicant.FirstName;
        application.LastName = applicant.LastName;
        application.Email = applicant.Email;
        application.Phone = applicant.PhoneNumber;

        db.RentalApplications.Add(application);
        await db.SaveChangesAsync(cancellationToken);
        return OperationResult<int>.Success(application.Id);
    }

    /// <summary>Validates and saves Section 1. Nothing is saved when it is invalid.</summary>
    public async Task<OperationResult> SaveApplicantInfoAsync(
        int id, string applicantId, ApplicantInfoInput input, CancellationToken cancellationToken = default)
    {
        var (application, denied) = await LoadEditableAsync(id, applicantId, includeResidences: false, cancellationToken);
        if (application is null)
        {
            return denied!;
        }

        input = ApplicationRules.Normalize(input);
        var errors = ApplicationRules.ValidateApplicantInfo(input);
        if (errors.Count > 0)
        {
            return OperationResult.Invalid(errors);
        }

        application.FirstName = input.FirstName;
        application.LastName = input.LastName;
        application.Phone = input.Phone;
        application.Email = input.Email;
        application.CurrentAddress ??= new Address();
        application.CurrentAddress.Street = input.Street!;
        application.CurrentAddress.City = input.City!;
        application.CurrentAddress.State = input.State!;
        application.CurrentAddress.PostalCode = input.PostalCode!;
        application.ApplicantInfoCompleted = true;
        return await SaveChangeAsync(application, cancellationToken);
    }

    /// <summary>Validates and adds a prior residence to the applicant's editable application.</summary>
    /// <param name="applicationId">The application to add to.</param>
    /// <param name="applicantId">The signed-in applicant (must own the application).</param>
    /// <param name="input">The residence details.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The new residence's id, or why it wasn't added (invalid, not found, not editable).</returns>
    public async Task<OperationResult<int>> AddResidenceAsync(
        int applicationId, string applicantId, ResidenceInput input, CancellationToken cancellationToken = default)
    {
        var (application, denied) = await LoadEditableAsync(applicationId, applicantId, includeResidences: false, cancellationToken);
        if (application is null)
        {
            return OperationResult<int>.From(denied!);
        }

        input = ApplicationRules.Normalize(input);
        var errors = ApplicationRules.ValidateResidence(input, clock.GetToday());
        if (errors.Count > 0)
        {
            return OperationResult<int>.From(OperationResult.Invalid(errors));
        }

        var residence = new Residence();
        Apply(residence, input);
        application.Residences.Add(residence);
        var saved = await SaveChangeAsync(application, cancellationToken);
        return saved.Succeeded ? OperationResult<int>.Success(residence.Id) : OperationResult<int>.From(saved);
    }

    /// <summary>Validates and saves changes to a residence of the applicant's editable application.</summary>
    /// <param name="residenceId">The residence to change.</param>
    /// <param name="applicantId">The signed-in applicant (must own the application).</param>
    /// <param name="input">The new residence details.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>Success, or why it wasn't saved (invalid, not found, not editable).</returns>
    public async Task<OperationResult> UpdateResidenceAsync(
        int residenceId, string applicantId, ResidenceInput input, CancellationToken cancellationToken = default)
    {
        var (application, residence, denied) = await LoadEditableResidenceAsync(residenceId, applicantId, cancellationToken);
        if (application is null || residence is null)
        {
            return denied!;
        }

        input = ApplicationRules.Normalize(input);
        var errors = ApplicationRules.ValidateResidence(input, clock.GetToday());
        if (errors.Count > 0)
        {
            return OperationResult.Invalid(errors);
        }

        Apply(residence, input);
        return await SaveChangeAsync(application, cancellationToken);
    }

    /// <summary>Removes a residence. Removing the last one makes Residence History incomplete again.</summary>
    public async Task<OperationResult> RemoveResidenceAsync(int residenceId, string applicantId, CancellationToken cancellationToken = default)
    {
        var (application, residence, denied) = await LoadEditableResidenceAsync(residenceId, applicantId, cancellationToken);
        if (application is null || residence is null)
        {
            return denied!;
        }

        db.Residences.Remove(residence);
        if (!await db.Residences.AnyAsync(r => r.RentalApplicationId == application.Id && r.Id != residenceId, cancellationToken))
        {
            application.ResidenceHistoryCompleted = false;
        }

        return await SaveChangeAsync(application, cancellationToken);
    }

    /// <summary>"Continue" on Section 2: valid when it has at least one residence and all of them are valid.</summary>
    public async Task<OperationResult> CompleteResidenceHistoryAsync(int id, string applicantId, CancellationToken cancellationToken = default)
    {
        var (application, denied) = await LoadEditableAsync(id, applicantId, includeResidences: true, cancellationToken);
        if (application is null)
        {
            return denied!;
        }

        var errors = ApplicationRules.ValidateResidenceHistory(
            application.Residences.Select(ApplicationRules.FromEntity).ToList(), clock.GetToday());
        if (errors.Count > 0)
        {
            return OperationResult.Invalid(errors);
        }

        application.ResidenceHistoryCompleted = true;
        return await SaveChangeAsync(application, cancellationToken);
    }

    /// <summary>
    /// Submits a Draft or resubmits a corrected Returned application. Both sections must have been saved and still
    /// be valid, and the unit must not have a lease covering today. Other open applications for the unit are left
    /// as they are.
    /// </summary>
    public async Task<OperationResult> SubmitAsync(int id, string applicantId, CancellationToken cancellationToken = default)
    {
        var (application, denied) = await LoadEditableAsync(id, applicantId, includeResidences: true, cancellationToken);
        if (application is null)
        {
            return denied!;
        }

        var blockers = await FindSubmitBlockersAsync(application, cancellationToken);
        if (blockers.Count > 0)
        {
            return OperationResult.Invalid(blockers.Select(b => new FieldError(null, b)).ToList());
        }

        application.TransitionTo(ApplicationStatus.Submitted, applicantId, clock.GetUtcNow());
        return await SaveAsync(cancellationToken);
    }

    /// <summary>Withdraws the applicant's own Draft, Submitted or Returned application. Withdrawn is terminal.</summary>
    public async Task<OperationResult> WithdrawAsync(int id, string applicantId, CancellationToken cancellationToken = default)
    {
        var application = await db.RentalApplications.SingleOrDefaultAsync(a => a.Id == id && a.ApplicantId == applicantId, cancellationToken);
        if (application is null)
        {
            return OperationResult.NotFound(NotFoundMessage);
        }

        if (!ApplicationStateMachine.CanTransition(application.Status, ApplicationStatus.Withdrawn))
        {
            return OperationResult.Conflict($"A {application.Status.InSentence()} application can't be withdrawn.");
        }

        application.TransitionTo(ApplicationStatus.Withdrawn, applicantId, clock.GetUtcNow());
        return await SaveAsync(cancellationToken);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    /// <summary>Loads the application for a change, or explains why it can't be changed.</summary>
    private async Task<(RentalApplication? Application, OperationResult? Denied)> LoadEditableAsync(
        int id, string applicantId, bool includeResidences, CancellationToken cancellationToken)
    {
        var query = db.RentalApplications.AsQueryable();
        if (includeResidences)
        {
            query = query.Include(a => a.Residences);
        }

        var application = await query.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (application is null || application.ApplicantId != applicantId)
        {
            return (null, OperationResult.NotFound(NotFoundMessage));
        }

        if (!CanApplicantEdit(application.Status, isOwner: true))
        {
            return (null, OperationResult.Conflict(
                $"This application is {application.Status.InSentence()} and can no longer be changed."));
        }

        return (application, null);
    }

    /// <summary>
    /// Loads a residence and its application for a change, applying the same ownership and status checks as
    /// <see cref="LoadEditableAsync"/>.
    /// </summary>
    /// <returns>The tracked application and residence, or the reason they can't be changed.</returns>
    private async Task<(RentalApplication? Application, Residence? Residence, OperationResult? Denied)> LoadEditableResidenceAsync(
        int residenceId, string applicantId, CancellationToken cancellationToken)
    {
        var applicationId = await db.Residences
            .Where(r => r.Id == residenceId)
            .Select(r => (int?)r.RentalApplicationId)
            .SingleOrDefaultAsync(cancellationToken);
        if (applicationId is null)
        {
            return (null, null, OperationResult.NotFound("Residence not found."));
        }

        var (application, denied) = await LoadEditableAsync(applicationId.Value, applicantId, includeResidences: false, cancellationToken);
        if (application is null)
        {
            return (null, null, denied);
        }

        var residence = await db.Residences.SingleAsync(r => r.Id == residenceId, cancellationToken);
        return (application, residence, null);
    }

    /// <summary>
    /// Everything that prevents submission: a section not saved yet, saved data that no longer passes the rules,
    /// or a lease covering today on the unit.
    /// </summary>
    /// <returns>User-facing messages; empty when the application may be submitted.</returns>
    private async Task<IReadOnlyList<string>> FindSubmitBlockersAsync(RentalApplication application, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        var today = clock.GetToday();

        if (!application.ApplicantInfoCompleted)
        {
            blockers.Add("Applicant Information has not been saved yet.");
        }
        else if (ApplicationRules.ValidateApplicantInfo(ApplicationRules.FromEntity(application)).Count > 0)
        {
            blockers.Add("Applicant Information has missing or invalid details. Go back and correct it.");
        }

        if (!application.ResidenceHistoryCompleted)
        {
            blockers.Add("Residence History has not been saved yet.");
        }
        else
        {
            blockers.AddRange(ApplicationRules
                .ValidateResidenceHistory(application.Residences.Select(ApplicationRules.FromEntity).ToList(), today)
                .Select(e => e.Message));
        }

        if (await UnitHasActiveLeaseAsync(application.UnitId, cancellationToken))
        {
            blockers.Add("This unit now has an active lease and can't accept new submissions.");
        }

        return blockers;
    }

    /// <summary>Whether any lease on the unit covers today (evaluated in SQL).</summary>
    private Task<bool> UnitHasActiveLeaseAsync(int unitId, CancellationToken cancellationToken) =>
        db.Leases.Where(l => l.UnitId == unitId).CoveringDay(clock.GetToday()).AnyAsync(cancellationToken);

    /// <summary>Copies validated input onto a residence entity, updating its owned address in place.</summary>
    private static void Apply(Residence residence, ResidenceInput input)
    {
        residence.Address.Street = input.Street!;
        residence.Address.City = input.City!;
        residence.Address.State = input.State!;
        residence.Address.PostalCode = input.PostalCode!;
        residence.LandlordName = input.LandlordName!;
        residence.LandlordPhone = input.LandlordPhone!;
        residence.MoveInDate = input.MoveInDate!.Value;
        residence.MoveOutDate = input.MoveOutDate!.Value;
    }

    /// <summary>Records a section change: bump the timestamp and rotate the concurrency stamp, then save.</summary>
    private Task<OperationResult> SaveChangeAsync(RentalApplication application, CancellationToken cancellationToken)
    {
        application.UpdatedAt = clock.GetUtcNow();
        application.ConcurrencyStamp = Guid.NewGuid();
        return SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Saves pending changes. A concurrency conflict (another change to the same application committed first) becomes
    /// a Conflict result asking the user to reload, instead of an exception.
    /// </summary>
    private async Task<OperationResult> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return OperationResult.Conflict(StaleMessage);
        }
    }
}
