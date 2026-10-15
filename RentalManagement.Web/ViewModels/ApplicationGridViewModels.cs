// =====================================================================================================================
// Module: RentalManagement.Web / ViewModels / ApplicationGridViewModels.cs
// Purpose: Models of the reusable application grid: the options a host view passes to the ApplicationGrid view
//          component, the component's own render model (columns, filter options, initial state), and the JSON contract
//          of GET /api/applications/grid and /api/applications/review-queue (one page of rows plus paging/sort metadata,
//          and for the queue the Claim / Review / Release actions of each row) that the grid script consumes.
// =====================================================================================================================

using System.Text.Json.Serialization;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services;
using RentalManagement.Core.Services.Applications;
using RentalManagement.Core.Services.Properties;

namespace RentalManagement.Web.ViewModels;

/// <summary>
/// What a host view passes to the <c>ApplicationGrid</c> view component. The rows themselves are always loaded from
/// the JSON endpoint for the signed-in user, so these options can only narrow or arrange what that user may see.
/// </summary>
public sealed class ApplicationGridOptions
{
    /// <summary>The grid's DOM id. Must be unique on the page when a view shows more than one grid.</summary>
    public string Id { get; init; } = "application-grid";

    /// <summary>The table caption (read by screen readers; shown when <see cref="ShowCaption"/>).</summary>
    public string Caption { get; init; } = "Applications";

    /// <summary>Whether the caption is visible, or only announced to screen readers.</summary>
    public bool ShowCaption { get; init; }

    /// <summary>The first page, sort and filters to show.</summary>
    public ApplicationListQuery Query { get; init; } = ApplicationListQuery.Default;

    /// <summary>Whether the status and property filters are shown. Without them the filters in <see cref="Query"/> are fixed.</summary>
    public bool ShowFilters { get; init; } = true;

    /// <summary>Whether the page-size selector is shown.</summary>
    public bool ShowPageSize { get; init; } = true;

    /// <summary>Whether the grid writes its state into the address bar (for the main list page, so it can be bookmarked).</summary>
    public bool SyncUrl { get; init; }

    /// <summary>A message shown above the grid, e.g. that invalid address parameters were reset.</summary>
    public string? Notice { get; init; }

    /// <summary>The empty-state text when no filter is applied; null for the role's default text.</summary>
    public string? EmptyText { get; init; }

    /// <summary>Whether the status filter is shown (with <see cref="ShowFilters"/>); off for the review queue, whose status is fixed.</summary>
    public bool ShowStatusFilter { get; init; } = true;

    /// <summary>
    /// Shows one of the property manager's review queues instead of the application list: rows come from
    /// <c>GET /api/applications/review-queue</c> and carry Claim / Review / Release actions. Renders nothing for
    /// anyone who isn't a property manager.
    /// </summary>
    public ReviewQueue? Queue { get; init; }
}

/// <summary>A column of the application grid.</summary>
/// <param name="Key">The sort key and the JSON field the cell shows (e.g. "unitNumber"); "action" for the link column.</param>
/// <param name="Label">The header text.</param>
/// <param name="CssClass">Responsive visibility classes, copied to every cell of the column.</param>
/// <param name="Sortable">Whether the header is a sort button.</param>
/// <param name="DefaultDirection">"asc" or "desc": the direction used when the column is first chosen.</param>
public sealed record ApplicationGridColumn(string Key, string Label, string CssClass, bool Sortable, string DefaultDirection);

/// <summary>Render model of the <c>ApplicationGrid</c> view component.</summary>
/// <param name="Options">The host's options.</param>
/// <param name="IsManager">Whether the viewer is a property manager (adds the applicant column).</param>
/// <param name="ApiUrl">The JSON endpoint the script calls.</param>
/// <param name="Columns">The visible columns, in order.</param>
/// <param name="PropertyOptions">The property filter's options (empty when filters are hidden).</param>
public sealed record ApplicationGridModel(
    ApplicationGridOptions Options,
    bool IsManager,
    string ApiUrl,
    IReadOnlyList<ApplicationGridColumn> Columns,
    IReadOnlyList<PropertyOption> PropertyOptions)
{
    /// <summary>The initial state.</summary>
    public ApplicationListQuery Query => Options.Query;

    /// <summary>The review queue's key ("available" / "myClaims") sent with every request, or null for the application list.</summary>
    public string? QueueKey => Options.Queue is { } queue ? ReviewQueueService.KeyOf(queue) : null;

    /// <summary>Every status, for the status filter (a status added to the enum appears automatically).</summary>
    public IReadOnlyList<ApplicationStatus> Statuses { get; } = Enum.GetValues<ApplicationStatus>();

    /// <summary>The allowed page sizes.</summary>
    public IReadOnlyList<int> PageSizes => ApplicationListQuery.AllowedPageSizes;

    /// <summary>Badge classes per status name, for the script to colour status cells like <c>_StatusBadge</c>.</summary>
    public IReadOnlyDictionary<string, string> StatusCss { get; } =
        Enum.GetValues<ApplicationStatus>().ToDictionary(s => s.ToString(), StatusDisplay.BadgeCss);
}

/// <summary>
/// Response of <c>GET /api/applications/grid</c>: one page of the application list and the paging and sort that
/// produced it.
/// </summary>
/// <param name="Page">The 1-based page number returned.</param>
/// <param name="PageSize">Rows per page.</param>
/// <param name="TotalCount">Applications matching the filters (for this user), before paging.</param>
/// <param name="TotalPages">Pages available at this page size; 0 when nothing matched.</param>
/// <param name="SortBy">The sort column key, e.g. "submittedDate".</param>
/// <param name="SortDirection">"asc" or "desc".</param>
/// <param name="Status">The status filter, or null.</param>
/// <param name="PropertyId">The property filter, or null.</param>
/// <param name="Items">The rows of this page; empty for a page beyond the last.</param>
public sealed record ApplicationGridResponse(
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    string SortBy,
    string SortDirection,
    string? Status,
    int? PropertyId,
    IReadOnlyList<ApplicationListRowViewModel> Items)
{
    /// <summary>Maps a service page to the response. Role-dependent fields are filled only for the roles that may see them.</summary>
    /// <param name="page">The page from <see cref="ApplicationService.ListPageAsync"/> or the review queue.</param>
    /// <param name="query">The query that produced it.</param>
    /// <param name="isManager">Whether the caller is a property manager.</param>
    /// <param name="urlFor">Builds the application page URL for an id.</param>
    /// <param name="actionsFor">For review-queue rows: the Claim / Review / Release URLs offered on each row; null elsewhere.</param>
    /// <returns>The response body.</returns>
    public static ApplicationGridResponse From(
        PagedResult<ApplicationListItem> page,
        ApplicationListQuery query,
        bool isManager,
        Func<int, string> urlFor,
        Func<ApplicationListItem, ReviewQueueActions?>? actionsFor = null) =>
        new(
            page.Page,
            page.PageSize,
            page.TotalCount,
            page.TotalPages,
            query.SortKey,
            query.SortDirectionKey,
            query.Status?.ToString(),
            query.PropertyId,
            page.Items
                .Select(item => ApplicationListRowViewModel.From(item, isManager, urlFor(item.Id), actionsFor?.Invoke(item)))
                .ToList());
}

/// <summary>
/// The actions a review-queue row offers, as URLs. Present only in <c>/api/applications/review-queue</c> rows. The
/// server re-checks every action on POST, so these are hints for the buttons, never permissions.
/// </summary>
/// <param name="ClaimUrl">POST target that claims the application (Available queue).</param>
/// <param name="ReviewUrl">The review modal (My claims queue).</param>
/// <param name="ReleaseUrl">The release confirmation modal (My claims queue).</param>
public sealed record ReviewQueueActions(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClaimUrl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewUrl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReleaseUrl);

/// <summary>One row of the application grid, as JSON.</summary>
/// <param name="Id">The application id.</param>
/// <param name="ApplicantName">The applicant's name; property managers only (omitted for applicants, who see only their own).</param>
/// <param name="PropertyName">The property's name.</param>
/// <param name="UnitNumber">The unit number.</param>
/// <param name="Status">The status name, e.g. "UnderReview".</param>
/// <param name="StatusLabel">The status for display, e.g. "Under Review".</param>
/// <param name="SubmittedDate">When it was (last) submitted; null for a draft never submitted.</param>
/// <param name="UpdatedDate">When it last changed.</param>
/// <param name="Url">The application page.</param>
/// <param name="ActionLabel">The link text: "Continue" (applicant, editable), "Review" (manager, awaiting review) or "Open".</param>
/// <param name="ClaimedByName">The manager holding the review claim; property managers only, omitted when unclaimed.</param>
/// <param name="ClaimedDate">When the claim was taken; property managers only, omitted when unclaimed.</param>
/// <param name="Actions">Review-queue row actions; omitted outside the review queue.</param>
public sealed record ApplicationListRowViewModel(
    int Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ApplicantName,
    string PropertyName,
    string UnitNumber,
    string Status,
    string StatusLabel,
    DateTimeOffset? SubmittedDate,
    DateTimeOffset UpdatedDate,
    string Url,
    string ActionLabel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClaimedByName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ClaimedDate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReviewQueueActions? Actions = null)
{
    /// <summary>Maps a service row for the given role. The claim is internal to the review team: never sent to applicants.</summary>
    /// <param name="item">The service row.</param>
    /// <param name="isManager">Whether the caller is a property manager.</param>
    /// <param name="url">The application page URL.</param>
    /// <param name="actions">Review-queue actions, or null outside the queue.</param>
    /// <returns>The JSON row.</returns>
    public static ApplicationListRowViewModel From(
        ApplicationListItem item, bool isManager, string url, ReviewQueueActions? actions = null) =>
        new(
            item.Id,
            isManager ? item.ApplicantName : null,
            item.PropertyName,
            item.UnitNumber,
            item.Status.ToString(),
            StatusDisplay.Label(item.Status),
            item.SubmittedAt,
            item.UpdatedAt,
            url,
            StatusDisplay.ListActionLabel(item.Status, isManager),
            isManager ? item.ClaimedByName : null,
            isManager ? item.ClaimedAt : null,
            isManager ? actions : null);
}

/// <summary>Model of the Applications/ReviewQueue page: the two queue grids.</summary>
public sealed class ReviewQueueViewModel
{
    /// <summary>"Available for review": Submitted, unclaimed applications, each with Claim.</summary>
    public required ApplicationGridOptions Available { get; init; }

    /// <summary>"My claimed applications": Under Review, claimed by the signed-in manager, with Review and Release.</summary>
    public required ApplicationGridOptions MyClaims { get; init; }
}
