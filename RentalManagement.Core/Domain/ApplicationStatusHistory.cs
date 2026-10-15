// =====================================================================================================================
// Module: RentalManagement.Core / Domain / ApplicationStatusHistory.cs
// Purpose: Audit trail entity. One row per status change of a rental application (including the draft's creation),
//          recording who made the change, when, the from/to statuses, and the review outcome and comment.
//          Rows are only ever written by RentalApplication.StartDraft and RentalApplication.TransitionTo.
// =====================================================================================================================

namespace RentalManagement.Core.Domain;

/// <summary>One status change of an application: who, when, from → to, and the review outcome and comment if any.</summary>
public class ApplicationStatusHistory
{
    public int Id { get; set; }

    public int RentalApplicationId { get; set; }

    public RentalApplication RentalApplication { get; set; } = null!;

    /// <summary>Null for the entry that records the draft being created.</summary>
    public ApplicationStatus? FromStatus { get; set; }

    public ApplicationStatus ToStatus { get; set; }

    public ReviewOutcome? Outcome { get; set; }

    public string? Comment { get; set; }

    public string ChangedById { get; set; } = string.Empty;

    public ApplicationUser ChangedBy { get; set; } = null!;

    public DateTimeOffset ChangedAt { get; set; }
}
