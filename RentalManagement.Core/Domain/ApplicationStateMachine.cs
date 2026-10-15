// =====================================================================================================================
// Module: RentalManagement.Core / Domain / ApplicationStateMachine.cs
// Purpose: Declares, as one transition table, every legal move between application statuses
//          (Draft → Submitted → Under Review (claim) → Approved/Returned/Denied, Under Review → Submitted (release),
//          Returned → Submitted, open statuses → Withdrawn).
//          RentalApplication.TransitionTo and the services consult it; nothing else decides status rules.
// =====================================================================================================================

namespace RentalManagement.Core.Domain;

/// <summary>
/// The single source of truth for which status transitions are legal. Kept as a table so the whole lifecycle is
/// readable in one place instead of being spread across controllers and services.
/// </summary>
public static class ApplicationStateMachine
{
    private static readonly IReadOnlyDictionary<ApplicationStatus, ApplicationStatus[]> Transitions =
        new Dictionary<ApplicationStatus, ApplicationStatus[]>
        {
            [ApplicationStatus.Draft] = [ApplicationStatus.Submitted, ApplicationStatus.Withdrawn],
            // A manager must claim it (→ UnderReview) before recording an outcome.
            [ApplicationStatus.Submitted] = [ApplicationStatus.UnderReview, ApplicationStatus.Withdrawn],
            // Back to Submitted is the release. The applicant may still withdraw while it is being reviewed.
            [ApplicationStatus.UnderReview] =
            [
                ApplicationStatus.Approved, ApplicationStatus.Returned, ApplicationStatus.Denied,
                ApplicationStatus.Submitted, ApplicationStatus.Withdrawn,
            ],
            [ApplicationStatus.Returned] = [ApplicationStatus.Submitted, ApplicationStatus.Withdrawn],
            [ApplicationStatus.Approved] = [],
            [ApplicationStatus.Denied] = [],
            [ApplicationStatus.Withdrawn] = [],
        };

    /// <summary>The statuses reachable from <paramref name="from"/> in one step.</summary>
    public static IReadOnlyList<ApplicationStatus> NextStatuses(ApplicationStatus from) => Transitions[from];

    /// <summary>Whether an application in status <paramref name="from"/> may move directly to <paramref name="to"/>.</summary>
    /// <param name="from">The current status.</param>
    /// <param name="to">The requested status.</param>
    /// <returns>True when the transition table allows the move.</returns>
    public static bool CanTransition(ApplicationStatus from, ApplicationStatus to) => Transitions[from].Contains(to);

    /// <summary>Approved, Denied and Withdrawn: no further transitions.</summary>
    public static bool IsTerminal(ApplicationStatus status) => Transitions[status].Length == 0;

    /// <summary>A review outcome (Approve/Return/Deny) can be recorded only while a manager holds the claim.</summary>
    public static bool IsReviewable(ApplicationStatus status) => status == ApplicationStatus.UnderReview;

    /// <summary>The applicant may change section data only in these statuses.</summary>
    public static bool IsEditableByApplicant(ApplicationStatus status) =>
        status is ApplicationStatus.Draft or ApplicationStatus.Returned;
}
