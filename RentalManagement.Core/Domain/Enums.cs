// =====================================================================================================================
// Module: RentalManagement.Core / Domain / Enums.cs
// Purpose: The application lifecycle (ApplicationStatus, with Under Review while a manager holds a claim) and review outcomes (ReviewOutcome), plus helpers that map an
//          outcome to the status it produces and say whether it needs a reviewer comment. Both enums are stored as
//          strings in the database (see ApplicationDbContext.ConfigureConventions).
// =====================================================================================================================

namespace RentalManagement.Core.Domain;

/// <summary>Lifecycle of a rental application. Stored as a string. Legal moves are in <see cref="ApplicationStateMachine"/>.</summary>
public enum ApplicationStatus
{
    Draft,
    Submitted,

    /// <summary>A property manager has claimed the submitted application and is reviewing it.</summary>
    UnderReview,
    Returned,
    Approved,
    Denied,
    Withdrawn,
}

/// <summary>The outcome a property manager records when completing a review.</summary>
public enum ReviewOutcome
{
    Approve,
    Return,
    Deny,
}

/// <summary>Maps review outcomes to the status they produce.</summary>
public static class ReviewOutcomeExtensions
{
    /// <summary>The status an application moves to when a manager records <paramref name="outcome"/>.</summary>
    /// <param name="outcome">The review outcome.</param>
    /// <returns>Approved, Returned or Denied.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined outcome.</exception>
    public static ApplicationStatus ToStatus(this ReviewOutcome outcome) => outcome switch
    {
        ReviewOutcome.Approve => ApplicationStatus.Approved,
        ReviewOutcome.Return => ApplicationStatus.Returned,
        ReviewOutcome.Deny => ApplicationStatus.Denied,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    /// <summary>Return and Deny need a comment so the applicant (and the history) knows why.</summary>
    public static bool RequiresComment(this ReviewOutcome outcome) => outcome is ReviewOutcome.Return or ReviewOutcome.Deny;
}

/// <summary>Display text for statuses, shared by service messages and the web layer.</summary>
public static class ApplicationStatusExtensions
{
    /// <summary>The status as words, e.g. <c>UnderReview</c> → "Under Review".</summary>
    /// <param name="status">The status.</param>
    /// <returns>The display name.</returns>
    public static string DisplayName(this ApplicationStatus status) =>
        System.Text.RegularExpressions.Regex.Replace(status.ToString(), "(?<=[a-z])(?=[A-Z])", " ");

    /// <summary>The display name in lower case, for use inside a sentence ("This application is under review").</summary>
    /// <param name="status">The status.</param>
    /// <returns>The lower-case display name.</returns>
    public static string InSentence(this ApplicationStatus status) => status.DisplayName().ToLowerInvariant();
}
