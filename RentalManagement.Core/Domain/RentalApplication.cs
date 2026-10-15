// =====================================================================================================================
// Module: RentalManagement.Core / Domain / RentalApplication.cs
// Purpose: The aggregate at the centre of the workflow: an applicant's application for one unit, holding Section 1
//          (applicant information), Section 2 (residences), per-section "completed" flags, the status with its
//          history, the property manager's review claim while Under Review, and the lease once approved. Status
//          changes are only possible through Claim, Release and TransitionTo, which share one private method that
//          enforces the state machine, records history and clears the claim in one place.
// =====================================================================================================================

namespace RentalManagement.Core.Domain;

/// <summary>
/// An applicant's application for one unit. It has two sections: Applicant Information (the fields on this
/// entity) and Residence History (<see cref="Residences"/>). Each section has a "completed" flag that is set only
/// when the section was saved in a valid state, and Submit requires both.
/// </summary>
public class RentalApplication
{
    public int Id { get; set; }

    public int UnitId { get; set; }

    public Unit Unit { get; set; } = null!;

    public string ApplicantId { get; set; } = string.Empty;

    public ApplicationUser Applicant { get; set; } = null!;

    /// <summary>Change it only through <see cref="TransitionTo"/>, which enforces the state machine and writes history.</summary>
    public ApplicationStatus Status { get; private set; } = ApplicationStatus.Draft;

    // Section 1 – Applicant Information. Nullable because a new draft starts empty.
    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public Address? CurrentAddress { get; set; }

    public bool ApplicantInfoCompleted { get; set; }

    // Section 2 – Residence History.
    public ICollection<Residence> Residences { get; set; } = [];

    public bool ResidenceHistoryCompleted { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }

    /// <summary>Optimistic concurrency token. Rotate it on every change so a stale save is rejected.</summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The property manager who holds the review claim; set only while the status is
    /// <see cref="ApplicationStatus.UnderReview"/> (a database check constraint enforces the pairing). Changed only by
    /// <see cref="Claim"/> and cleared by every transition out of Under Review.
    /// </summary>
    public string? ClaimedByUserId { get; private set; }

    /// <summary>The claiming property manager.</summary>
    public ApplicationUser? ClaimedBy { get; private set; }

    /// <summary>When the current claim was taken (UTC). Null whenever <see cref="ClaimedByUserId"/> is null.</summary>
    public DateTimeOffset? ClaimedAt { get; private set; }

    public ICollection<ApplicationStatusHistory> StatusHistory { get; set; } = [];

    /// <summary>Set once the application is approved.</summary>
    public Lease? Lease { get; set; }

    public bool IsReadyToSubmit => ApplicantInfoCompleted && ResidenceHistoryCompleted;

    /// <summary>Whether <paramref name="userId"/> currently holds the review claim.</summary>
    /// <param name="userId">The user to check.</param>
    /// <returns>True when the application is Under Review and claimed by that user.</returns>
    public bool IsClaimedBy(string userId) => Status == ApplicationStatus.UnderReview && ClaimedByUserId == userId;

    /// <summary>Starts a new draft and records its creation in the history.</summary>
    public static RentalApplication StartDraft(int unitId, string applicantId, DateTimeOffset now)
    {
        var application = new RentalApplication
        {
            UnitId = unitId,
            ApplicantId = applicantId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        application.StatusHistory.Add(new ApplicationStatusHistory
        {
            FromStatus = null,
            ToStatus = ApplicationStatus.Draft,
            ChangedById = applicantId,
            ChangedAt = now,
        });
        return application;
    }

    /// <summary>
    /// A property manager claims a Submitted, unclaimed application: Submitted → Under Review, recording the claim
    /// owner and time and a history row. The caller (ApplicationReviewService) has already checked the role and
    /// reports friendly errors; reaching this with an illegal claim is a bug, so it throws.
    /// </summary>
    /// <param name="managerId">The claiming property manager's user id.</param>
    /// <param name="now">The claim time (UTC).</param>
    /// <returns>The history row written.</returns>
    /// <exception cref="InvalidOperationException">The application is not Submitted or is already claimed.</exception>
    public ApplicationStatusHistory Claim(string managerId, DateTimeOffset now)
    {
        if (Status != ApplicationStatus.Submitted || ClaimedByUserId is not null)
        {
            throw new InvalidOperationException($"Application {Id} cannot be claimed while {Status}.");
        }

        var entry = Apply(ApplicationStatus.UnderReview, managerId, now, outcome: null, comment: null);
        ClaimedByUserId = managerId;
        ClaimedAt = now;
        return entry;
    }

    /// <summary>
    /// The claim owner gives the application back to the queue: Under Review → Submitted, clearing the claim. The
    /// original submission date is kept, so a released application keeps its place in the queue.
    /// </summary>
    /// <param name="managerId">The releasing manager; must be the claim owner.</param>
    /// <param name="now">The release time (UTC).</param>
    /// <param name="comment">An optional note for the history.</param>
    /// <returns>The history row written.</returns>
    /// <exception cref="InvalidOperationException">The application isn't claimed by <paramref name="managerId"/>.</exception>
    public ApplicationStatusHistory Release(string managerId, DateTimeOffset now, string? comment = null)
    {
        if (!IsClaimedBy(managerId))
        {
            throw new InvalidOperationException($"Application {Id} is not claimed by user {managerId}.");
        }

        return Apply(ApplicationStatus.Submitted, managerId, now, outcome: null, comment);
    }

    /// <summary>
    /// The only way to change <see cref="Status"/> other than <see cref="Claim"/> and <see cref="Release"/>. Throws
    /// when the state machine forbids the move, because callers (services) check first and report a friendly error;
    /// reaching this with an illegal move is a bug. A review outcome may only be recorded by the claim owner.
    /// </summary>
    public ApplicationStatusHistory TransitionTo(
        ApplicationStatus to, string changedById, DateTimeOffset now, ReviewOutcome? outcome = null, string? comment = null)
    {
        if (to == ApplicationStatus.UnderReview || (Status == ApplicationStatus.UnderReview && to == ApplicationStatus.Submitted))
        {
            throw new InvalidOperationException($"Use {nameof(Claim)} or {nameof(Release)} to move application {Id} from {Status} to {to}.");
        }

        if (outcome is not null && !IsClaimedBy(changedById))
        {
            throw new InvalidOperationException($"Only the manager who claimed application {Id} can record a review outcome.");
        }

        return Apply(to, changedById, now, outcome, comment);
    }

    /// <summary>
    /// Applies a legal move: writes the history row, updates the status and timestamps, clears the claim when leaving
    /// Under Review, and rotates the concurrency stamp.
    /// </summary>
    private ApplicationStatusHistory Apply(
        ApplicationStatus to, string changedById, DateTimeOffset now, ReviewOutcome? outcome, string? comment)
    {
        if (!ApplicationStateMachine.CanTransition(Status, to))
        {
            throw new InvalidOperationException($"Application {Id} cannot move from {Status} to {to}.");
        }

        var entry = new ApplicationStatusHistory
        {
            FromStatus = Status,
            ToStatus = to,
            Outcome = outcome,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            ChangedById = changedById,
            ChangedAt = now,
        };
        StatusHistory.Add(entry);

        // Only a real (re)submission sets the submission date; a release puts it back in the queue where it was.
        if (to == ApplicationStatus.Submitted && Status != ApplicationStatus.UnderReview)
        {
            SubmittedAt = now;
        }

        if (Status == ApplicationStatus.UnderReview)
        {
            ClaimedByUserId = null;
            ClaimedBy = null;
            ClaimedAt = null;
        }

        Status = to;
        UpdatedAt = now;
        ConcurrencyStamp = Guid.NewGuid();
        return entry;
    }
}
