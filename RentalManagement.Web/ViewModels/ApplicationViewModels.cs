// =====================================================================================================================
// Module: RentalManagement.Web / ViewModels / ApplicationViewModels.cs
// Purpose: View models of the rental application page: the section enum, the single page view model posted by the
//          one form, the server-side header, the Applicant Information section, the residence modal form, the
//          property manager's review modal form, the view component models (residence list, status card, status
//          history), the application list page's query parameters, and status/section display helpers. Server-decided
//          values are [BindNever] so a post can't change them.
// =====================================================================================================================

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Rendering;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services;
using RentalManagement.Core.Services.Applications;

namespace RentalManagement.Web.ViewModels;

/// <summary>The sections of the application page, in order.</summary>
public enum ApplicationSection
{
    ApplicantInfo,
    ResidenceHistory,
    Summary,
}

/// <summary>
/// The single view model for the whole application page. One form posts it to one action; the clicked button
/// (<c>command</c> = continue / back / submit) decides what happens. Only <see cref="CurrentSection"/> and
/// <see cref="ApplicantInfo"/> are bound from the post. Everything the server decides (<see cref="Header"/>,
/// editability, submit blockers) is <c>[BindNever]</c> and reloaded from the database on every request.
/// </summary>
public sealed class ApplicationPageViewModel
{
    public int Id { get; set; }

    /// <summary>The section that was on screen when the form was posted.</summary>
    public ApplicationSection CurrentSection { get; set; }

    /// <summary>Section 1. Bound and validated only when Section 1 is being continued.</summary>
    public ApplicantInfoViewModel ApplicantInfo { get; set; } = new();

    /// <summary>Always set by the controller before a view renders; null only while the post is being bound.</summary>
    [BindNever, ValidateNever]
    public ApplicationHeader Header { get; set; } = null!;

    /// <summary>Reasons Submit is disabled, shown on the Summary.</summary>
    [BindNever, ValidateNever]
    public IReadOnlyList<string> SubmitBlockers { get; set; } = [];

    // Computed from the server-side Header. [ValidateNever]: model validation reads every property of the bound
    // model, and Header is not set yet at that point (it is [BindNever] and loaded by the controller afterwards).

    [ValidateNever]
    public bool IsEditable => Header?.IsEditable == true;

    [ValidateNever]
    public bool CanSubmit => IsEditable && SubmitBlockers.Count == 0;

    [ValidateNever]
    public ApplicationSection? PreviousSection => CurrentSection == ApplicationSection.ApplicantInfo ? null : CurrentSection - 1;

    [ValidateNever]
    public ApplicationSection? NextSection => CurrentSection == ApplicationSection.Summary ? null : CurrentSection + 1;
}

/// <summary>Server-side facts about the application shown around the sections. Never bound from a post.</summary>
public sealed record ApplicationHeader(ApplicationDetails Details, bool IsOwner, bool IsEditable, bool IsManager)
{
    /// <summary>Whether a section has been saved in a valid state (shown as "Saved" in the step indicator).</summary>
    /// <param name="section">The section to check; the Summary is never "saved".</param>
    /// <returns>True when the section's completed flag is set.</returns>
    public bool IsSectionComplete(ApplicationSection section) => section switch
    {
        ApplicationSection.ApplicantInfo => Details.ApplicantInfoCompleted,
        ApplicationSection.ResidenceHistory => Details.ResidenceHistoryCompleted,
        _ => false,
    };
}

/// <summary>
/// Section 1 – Applicant Information. The same partial renders it editable or read-only; <see cref="IsEditable"/>
/// is decided on the server and can't be set by a post.
/// </summary>
public sealed class ApplicantInfoViewModel
{
    [Required, StringLength(ApplicationRules.NameLength), Display(Name = "First name")]
    public string? FirstName { get; set; }

    [Required, StringLength(ApplicationRules.NameLength), Display(Name = "Last name")]
    public string? LastName { get; set; }

    [Required, Phone, StringLength(ApplicationRules.PhoneLength)]
    public string? Phone { get; set; }

    [Required, EmailAddress, StringLength(ApplicationRules.EmailLength)]
    public string? Email { get; set; }

    [Required, StringLength(200)]
    public string? Street { get; set; }

    [Required, StringLength(100)]
    public string? City { get; set; }

    [Required, StringLength(50)]
    public string? State { get; set; }

    [Required, StringLength(20), Display(Name = "Postal code")]
    public string? PostalCode { get; set; }

    [BindNever, ValidateNever]
    public bool IsEditable { get; set; }

    /// <summary>The section as the service's input record.</summary>
    /// <returns>The applicant information input.</returns>
    public ApplicantInfoInput ToInput() => new(FirstName, LastName, Phone, Email, Street, City, State, PostalCode);

    /// <summary>The section model for saved data.</summary>
    /// <param name="input">The applicant information loaded from the database.</param>
    /// <param name="isEditable">The server's decision whether to render inputs or read-only text.</param>
    /// <returns>The section model.</returns>
    public static ApplicantInfoViewModel From(ApplicantInfoInput input, bool isEditable) => new()
    {
        FirstName = input.FirstName,
        LastName = input.LastName,
        Phone = input.Phone,
        Email = input.Email,
        Street = input.Street,
        City = input.City,
        State = input.State,
        PostalCode = input.PostalCode,
        IsEditable = isEditable,
    };

    /// <summary>The same data rendered read-only (used by the Summary).</summary>
    /// <returns>A read-only copy.</returns>
    public ApplicantInfoViewModel AsReadOnly() => From(ToInput(), isEditable: false);
}

/// <summary>The add/edit residence modal. Property names match <see cref="ResidenceInput"/>.</summary>
public sealed class ResidenceFormViewModel : IValidatableObject
{
    /// <summary>Null when adding.</summary>
    public int? Id { get; set; }

    public int ApplicationId { get; set; }

    [Required, StringLength(200)]
    public string? Street { get; set; }

    [Required, StringLength(100)]
    public string? City { get; set; }

    [Required, StringLength(50)]
    public string? State { get; set; }

    [Required, StringLength(20), Display(Name = "Postal code")]
    public string? PostalCode { get; set; }

    [Required, StringLength(ApplicationRules.LandlordNameLength), Display(Name = "Landlord name")]
    public string? LandlordName { get; set; }

    [Required, Phone, StringLength(ApplicationRules.PhoneLength), Display(Name = "Landlord phone")]
    public string? LandlordPhone { get; set; }

    [Required, DataType(DataType.Date), Display(Name = "Move-in date")]
    public DateOnly? MoveInDate { get; set; }

    [Required, DataType(DataType.Date), Display(Name = "Move-out date")]
    public DateOnly? MoveOutDate { get; set; }

    public bool IsNew => Id is null;

    /// <summary>The form as the service's input record.</summary>
    /// <returns>The residence input.</returns>
    public ResidenceInput ToInput() =>
        new(Street, City, State, PostalCode, LandlordName, LandlordPhone, MoveInDate, MoveOutDate);

    /// <summary>A pre-filled edit form for an existing residence.</summary>
    /// <param name="details">The residence loaded from the database.</param>
    /// <returns>The form model.</returns>
    public static ResidenceFormViewModel From(ResidenceDetails details) => new()
    {
        Id = details.Id,
        ApplicationId = details.ApplicationId,
        Street = details.Input.Street,
        City = details.Input.City,
        State = details.Input.State,
        PostalCode = details.Input.PostalCode,
        LandlordName = details.Input.LandlordName,
        LandlordPhone = details.Input.LandlordPhone,
        MoveInDate = details.Input.MoveInDate,
        MoveOutDate = details.Input.MoveOutDate,
    };

    /// <summary>Cross-field check for a quick message; the service re-checks this and the "not in the future" rules.</summary>
    /// <param name="validationContext">Supplied by MVC model validation.</param>
    /// <returns>An error on <see cref="MoveOutDate"/> when it is before <see cref="MoveInDate"/>.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MoveInDate is { } moveIn && MoveOutDate is { } moveOut && moveOut < moveIn)
        {
            yield return new ValidationResult("Move-out date must be on or after the move-in date.", [nameof(MoveOutDate)]);
        }
    }
}

/// <summary>
/// The review modal: outcome, comment and lease start date. Property names match <see cref="ReviewInput"/> so service
/// errors land on the right field. The context shown in the dialog (applicant, unit, rent) is reloaded on the server.
/// </summary>
public sealed class ReviewFormViewModel : IValidatableObject
{
    /// <summary>The application being reviewed (from the route).</summary>
    public int ApplicationId { get; set; }

    /// <summary>Approve, Return or Deny.</summary>
    [Required(ErrorMessage = "Choose an outcome: Approve, Return or Deny.")]
    public ReviewOutcome? Outcome { get; set; }

    /// <summary>Required for Return and Deny; shown to the applicant and kept in the history.</summary>
    [StringLength(ApplicationRules.CommentLength)]
    [DataType(DataType.MultilineText)]
    public string? Comment { get; set; }

    /// <summary>First day of the twelve-month lease; used only when approving.</summary>
    [DataType(DataType.Date), Display(Name = "Lease start date")]
    public DateOnly? LeaseStartDate { get; set; }

    /// <summary>"Unit 101 · Maple Court", for the dialog title. Reloaded on the server, never bound.</summary>
    [BindNever, ValidateNever]
    public string UnitLabel { get; set; } = string.Empty;

    /// <summary>The applicant's name, for the dialog. Reloaded on the server, never bound.</summary>
    [BindNever, ValidateNever]
    public string ApplicantName { get; set; } = string.Empty;

    /// <summary>The unit's current rent, which the lease will use. Reloaded on the server, never bound.</summary>
    [BindNever, ValidateNever]
    public decimal MonthlyRent { get; set; }

    /// <summary>The form as the service's input record.</summary>
    /// <returns>The review input.</returns>
    public ReviewInput ToInput() => new(Outcome, Comment, LeaseStartDate);

    /// <summary>
    /// Quick cross-field checks for a friendly message before calling the service (which re-checks everything): a
    /// comment for Return/Deny, a start date for Approve.
    /// </summary>
    /// <param name="validationContext">Supplied by MVC model validation.</param>
    /// <returns>Errors on <see cref="Comment"/> or <see cref="LeaseStartDate"/>.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Outcome is { } outcome && outcome.RequiresComment() && string.IsNullOrWhiteSpace(Comment))
        {
            yield return new ValidationResult(
                $"A comment is required to {outcome.ToString().ToLowerInvariant()} an application, so the applicant knows why.",
                [nameof(Comment)]);
        }

        if (Outcome == ReviewOutcome.Approve && LeaseStartDate is null)
        {
            yield return new ValidationResult("Lease start date is required to approve.", [nameof(LeaseStartDate)]);
        }
    }
}

/// <summary>
/// The application list page. The list parameters (status, property, page, page size, sort) bind from the query string
/// so a list URL can be bookmarked; they only set the grid's first state, which the grid then loads from the JSON
/// endpoint. Which rows a user may see is decided by the service from the signed-in user, never by the query string.
/// </summary>
public sealed class ApplicationListViewModel
{
    /// <summary>Show only applications in this status (a status name, e.g. "Submitted").</summary>
    public string? Status { get; set; }

    /// <summary>Show only applications for units of this property.</summary>
    [Display(Name = "Property")]
    public int? PropertyId { get; set; }

    /// <summary>The 1-based page number.</summary>
    public int? Page { get; set; }

    /// <summary>Rows per page (10, 25, 50 or 100).</summary>
    public int? PageSize { get; set; }

    /// <summary>The sort column key, e.g. "propertyName".</summary>
    public string? SortBy { get; set; }

    /// <summary>"asc" or "desc".</summary>
    public string? SortDirection { get; set; }

    /// <summary>Whether the viewer is a property manager (changes the title).</summary>
    [BindNever, ValidateNever]
    public bool IsManager { get; set; }

    /// <summary>The grid's options, built on the server from the validated parameters.</summary>
    [BindNever, ValidateNever]
    public ApplicationGridOptions Grid { get; set; } = new();

    /// <summary>Validates the bound parameters with the service's rules.</summary>
    /// <returns>The query, or why the parameters are invalid.</returns>
    public OperationResult<ApplicationListQuery> ToQuery() =>
        ApplicationListQuery.Create(Page, PageSize, SortBy, SortDirection, Status, PropertyId);
}

/// <summary>Model of the StatusHistory view component.</summary>
/// <param name="ApplicationId">The application whose history is shown.</param>
/// <param name="Entries">The history, oldest first.</param>
public sealed record StatusHistoryModel(int ApplicationId, IReadOnlyList<StatusHistoryEntry> Entries);

/// <summary>Model of the ResidenceList view component.</summary>
public sealed record ResidenceListModel(int ApplicationId, IReadOnlyList<ResidenceDetails> Residences, bool IsEditable);

/// <summary>Model of the ApplicationStatus view component (the status card).</summary>
/// <param name="Details">The application.</param>
/// <param name="CanWithdraw">Whether to offer Withdraw (the signed-in applicant owns it and it is still open).</param>
/// <param name="IsManager">Whether the viewer is a property manager.</param>
/// <param name="CanReview">Whether to offer Review and Release (the property manager who holds the claim).</param>
/// <param name="CanClaim">Whether to offer Claim (a property manager viewing a Submitted application).</param>
public sealed record ApplicationStatusModel(ApplicationDetails Details, bool CanWithdraw, bool IsManager, bool CanReview, bool CanClaim);

/// <summary>Display helpers for statuses.</summary>
public static class StatusDisplay
{
    /// <summary>The Bootstrap badge classes for a status (e.g. green for Approved, amber for Returned).</summary>
    /// <param name="status">The application status.</param>
    /// <returns>The CSS classes.</returns>
    public static string BadgeCss(ApplicationStatus status) => status switch
    {
        ApplicationStatus.Draft => "text-bg-light border",
        ApplicationStatus.Submitted => "text-bg-primary",
        ApplicationStatus.UnderReview => "text-bg-info",
        ApplicationStatus.Returned => "text-bg-warning",
        ApplicationStatus.Approved => "text-bg-success",
        ApplicationStatus.Denied => "text-bg-danger",
        ApplicationStatus.Withdrawn => "text-bg-secondary",
        _ => "text-bg-light",
    };

    /// <summary>A status for display: the enum name split into words, e.g. "UnderReview" → "Under Review".</summary>
    /// <param name="status">The application status.</param>
    /// <returns>The label.</returns>
    public static string Label(ApplicationStatus status) => status.DisplayName();

    /// <summary>
    /// The list's link text, hinting at the next step: applicants continue an application they can still edit,
    /// managers review one that is waiting for a decision (to claim, or under review), everything else is opened
    /// read-only.
    /// </summary>
    /// <param name="status">The application status.</param>
    /// <param name="isManager">Whether the viewer is a property manager.</param>
    /// <returns>"Continue", "Review" or "Open".</returns>
    public static string ListActionLabel(ApplicationStatus status, bool isManager) =>
        !isManager && ApplicationStateMachine.IsEditableByApplicant(status) ? "Continue"
        : isManager && status is ApplicationStatus.Submitted or ApplicationStatus.UnderReview ? "Review"
        : "Open";

    /// <summary>The heading shown for a section.</summary>
    /// <param name="section">The section.</param>
    /// <returns>The title, e.g. "Residence history".</returns>
    public static string SectionTitle(ApplicationSection section) => section switch
    {
        ApplicationSection.ApplicantInfo => "Applicant information",
        ApplicationSection.ResidenceHistory => "Residence history",
        _ => "Summary",
    };
}
