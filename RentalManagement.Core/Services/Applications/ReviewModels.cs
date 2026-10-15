// =====================================================================================================================
// Module: RentalManagement.Core / Services / Applications / ReviewModels.cs
// Purpose: The data contracts of ApplicationReviewService: the property manager's review decision (outcome, comment,
//          lease start date) and the status-history entries shown to property managers on the application page.
// =====================================================================================================================

using RentalManagement.Core.Domain;

namespace RentalManagement.Core.Services.Applications;

/// <summary>A property manager's decision on a submitted application.</summary>
/// <param name="Outcome">Approve, Return or Deny; null when none was chosen.</param>
/// <param name="Comment">The reviewer's comment; required for Return and Deny.</param>
/// <param name="LeaseStartDate">First day of the twelve-month lease; required for Approve, ignored otherwise.</param>
public sealed record ReviewInput(ReviewOutcome? Outcome, string? Comment, DateOnly? LeaseStartDate);

/// <summary>One row of an application's status history, as shown to property managers.</summary>
/// <param name="ChangedAt">When the change happened.</param>
/// <param name="ChangedByName">Full name of the user who made the change.</param>
/// <param name="ChangedByEmail">Email of that user.</param>
/// <param name="FromStatus">The previous status; null for the draft's creation.</param>
/// <param name="ToStatus">The new status.</param>
/// <param name="Outcome">The review outcome, for changes made by a review.</param>
/// <param name="Comment">The reviewer's comment, if any.</param>
public sealed record StatusHistoryEntry(
    DateTimeOffset ChangedAt,
    string ChangedByName,
    string? ChangedByEmail,
    ApplicationStatus? FromStatus,
    ApplicationStatus ToStatus,
    ReviewOutcome? Outcome,
    string? Comment);
