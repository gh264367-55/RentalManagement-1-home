// =====================================================================================================================
// Module: RentalManagement.Core / Services / Applications / ApplicationModels.cs
// Purpose: The data contracts of ApplicationService: section inputs (applicant info, residence), and read models
//          for the application page (details with lease and latest review), residences and the application list.
// =====================================================================================================================

using RentalManagement.Core.Domain;

namespace RentalManagement.Core.Services.Applications;

// Inputs and read models for ApplicationService. Input property names are the field names used in errors.

/// <summary>Section 1 – Applicant Information.</summary>
public sealed record ApplicantInfoInput(
    string? FirstName,
    string? LastName,
    string? Phone,
    string? Email,
    string? Street,
    string? City,
    string? State,
    string? PostalCode);

/// <summary>One prior residence in Section 2 – Residence History.</summary>
public sealed record ResidenceInput(
    string? Street,
    string? City,
    string? State,
    string? PostalCode,
    string? LandlordName,
    string? LandlordPhone,
    DateOnly? MoveInDate,
    DateOnly? MoveOutDate);

/// <summary>A saved residence.</summary>
public sealed record ResidenceDetails(int Id, int ApplicationId, ResidenceInput Input);

/// <summary>The latest review outcome, shown to the applicant so they know what to fix (Returned) or why (Denied).</summary>
public sealed record ReviewNote(ReviewOutcome Outcome, string? Comment, string ReviewerName, DateTimeOffset At);

/// <summary>The lease issued on approval.</summary>
public sealed record LeaseSummary(DateOnly StartDate, DateOnly EndDate, decimal MonthlyRent);

/// <summary>Everything the application page needs about one application, loaded in one query.</summary>
public sealed record ApplicationDetails(
    int Id,
    ApplicationStatus Status,
    string ApplicantId,
    string ApplicantName,
    int UnitId,
    string UnitNumber,
    string PropertyName,
    string PropertyAddress,
    decimal MonthlyRent,
    ApplicantInfoInput ApplicantInfo,
    bool ApplicantInfoCompleted,
    bool ResidenceHistoryCompleted,
    int ResidenceCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SubmittedAt,
    LeaseSummary? Lease,
    ReviewNote? LatestReview,
    bool UnitHasActiveLease,
    string? ClaimedByUserId,
    string? ClaimedByName,
    DateTimeOffset? ClaimedAt)
{
    /// <summary>Whether <paramref name="userId"/> holds the review claim (and so may release or review it).</summary>
    /// <param name="userId">The signed-in user's id.</param>
    /// <returns>True when Under Review and claimed by that user.</returns>
    public bool IsClaimedBy(string userId) => Status == ApplicationStatus.UnderReview && ClaimedByUserId == userId;

    /// <summary>Whether <paramref name="userId"/> may change this application's sections now (owner + Draft/Returned).</summary>
    /// <param name="userId">The signed-in user's id.</param>
    /// <returns>True when the sections are editable for that user.</returns>
    public bool IsEditableBy(string userId) => ApplicationService.CanApplicantEdit(Status, ApplicantId == userId);

    /// <summary>Whether <paramref name="userId"/> may withdraw it now (owner + a status that allows withdrawal).</summary>
    /// <param name="userId">The signed-in user's id.</param>
    /// <returns>True when Withdraw should be offered.</returns>
    public bool IsWithdrawableBy(string userId) =>
        ApplicantId == userId && ApplicationStateMachine.CanTransition(Status, ApplicationStatus.Withdrawn);
}

/// <summary>Criteria for the application list. Every criterion is optional.</summary>
/// <param name="Status">Only applications in this status.</param>
/// <param name="PropertyId">Only applications for units of this property.</param>
public sealed record ApplicationListFilter(ApplicationStatus? Status = null, int? PropertyId = null);

/// <summary>A row of the application list (and of the review queue).</summary>
/// <param name="Id">The application id.</param>
/// <param name="Status">The current status.</param>
/// <param name="ApplicantName">The applicant's account name.</param>
/// <param name="PropertyName">The property's name.</param>
/// <param name="UnitNumber">The unit number.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="SubmittedAt">When it was last (re)submitted (a release keeps it); null for a draft never submitted.</param>
/// <param name="ClaimedByName">The property manager holding the review claim; null unless Under Review.</param>
/// <param name="ClaimedAt">When the claim was taken (UTC); null unless Under Review.</param>
public sealed record ApplicationListItem(
    int Id,
    ApplicationStatus Status,
    string ApplicantName,
    string PropertyName,
    string UnitNumber,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SubmittedAt,
    string? ClaimedByName,
    DateTimeOffset? ClaimedAt);

/// <summary>The two lists of the property manager's review queue.</summary>
public enum ReviewQueue
{
    /// <summary>Submitted applications nobody has claimed yet.</summary>
    Available,

    /// <summary>Applications Under Review that the signed-in manager has claimed.</summary>
    MyClaims,
}
