// =====================================================================================================================
// Module: RentalManagement.Core / Services / Applications / ApplicationListQuery.cs
// Purpose: The validated request for one page of the application list: page number, page size, sort column and
//          direction, and the status / property filters. Raw values (query string, JSON endpoint) are parsed and
//          checked against whitelists here, once, so the controller, the grid view component and the tests share the
//          same rules and nothing user-supplied ever reaches the query except through these typed values.
// =====================================================================================================================

using RentalManagement.Core.Domain;

namespace RentalManagement.Core.Services.Applications;

/// <summary>The columns the application list can be sorted by. Each maps to a fixed expression in the service.</summary>
public enum ApplicationSortColumn
{
    Id,
    ApplicantName,
    PropertyName,
    UnitNumber,
    Status,
    SubmittedDate,
    UpdatedDate,

    /// <summary>When the review claim was taken; applications without a claim sort last.</summary>
    ClaimedDate,
}

/// <summary>Sort direction.</summary>
public enum SortDirection
{
    Ascending,
    Descending,
}

/// <summary>
/// A validated request for one page of the application list. Instances come only from <see cref="Create"/> (or
/// <see cref="Default"/>), so a query object always holds an allowed page size, a page of at least 1 and a
/// whitelisted sort.
/// </summary>
public sealed record ApplicationListQuery
{
    /// <summary>The first page.</summary>
    public const int DefaultPage = 1;

    /// <summary>Rows per page when none is requested.</summary>
    public const int DefaultPageSize = 10;

    /// <summary>Default sort column: most recently submitted first.</summary>
    public const ApplicationSortColumn DefaultSortBy = ApplicationSortColumn.SubmittedDate;

    /// <summary>Default sort direction for <see cref="DefaultSortBy"/>.</summary>
    public const SortDirection DefaultSortDirection = SortDirection.Descending;

    /// <summary>The page sizes a caller may ask for.</summary>
    public static IReadOnlyList<int> AllowedPageSizes { get; } = [10, 25, 50, 100];

    /// <summary>
    /// The public names of the sort columns (query string and JSON), matched case-insensitively. This dictionary is
    /// the whitelist: any other value is rejected.
    /// </summary>
    public static IReadOnlyDictionary<string, ApplicationSortColumn> SortKeys { get; } =
        new Dictionary<string, ApplicationSortColumn>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ApplicationSortColumn.Id,
            ["applicantName"] = ApplicationSortColumn.ApplicantName,
            ["propertyName"] = ApplicationSortColumn.PropertyName,
            ["unitNumber"] = ApplicationSortColumn.UnitNumber,
            ["status"] = ApplicationSortColumn.Status,
            ["submittedDate"] = ApplicationSortColumn.SubmittedDate,
            ["updatedDate"] = ApplicationSortColumn.UpdatedDate,
            ["claimedDate"] = ApplicationSortColumn.ClaimedDate,
        };

    private ApplicationListQuery()
    {
    }

    /// <summary>Page 1, 10 rows, most recently submitted first, no filters.</summary>
    public static ApplicationListQuery Default { get; } = new();

    /// <summary>The 1-based page number.</summary>
    public int Page { get; private init; } = DefaultPage;

    /// <summary>Rows per page; one of <see cref="AllowedPageSizes"/>.</summary>
    public int PageSize { get; private init; } = DefaultPageSize;

    /// <summary>The sort column.</summary>
    public ApplicationSortColumn SortBy { get; private init; } = DefaultSortBy;

    /// <summary>The sort direction.</summary>
    public SortDirection SortDirection { get; private init; } = DefaultSortDirection;

    /// <summary>Only applications in this status, or null for any status.</summary>
    public ApplicationStatus? Status { get; private init; }

    /// <summary>Only applications for units of this property, or null for any property.</summary>
    public int? PropertyId { get; private init; }

    /// <summary>The status and property criteria as a filter.</summary>
    public ApplicationListFilter Filter => new(Status, PropertyId);

    /// <summary>The number of rows before this page (64-bit, so a huge page number can't overflow).</summary>
    public long Skip => (long)(Page - 1) * PageSize;

    /// <summary>The public name of the sort column, e.g. "submittedDate".</summary>
    public string SortKey => SortKeyOf(SortBy);

    /// <summary>"asc" or "desc".</summary>
    public string SortDirectionKey => SortDirection == SortDirection.Ascending ? "asc" : "desc";

    /// <summary>The public name of a sort column.</summary>
    /// <param name="column">The column.</param>
    /// <returns>The key used in the query string and JSON, e.g. "unitNumber".</returns>
    public static string SortKeyOf(ApplicationSortColumn column) => SortKeys.First(k => k.Value == column).Key;

    /// <summary>
    /// The direction used when a column is chosen without one: newest first for dates, A→Z / lowest first otherwise.
    /// </summary>
    /// <param name="column">The column.</param>
    /// <returns>The column's natural first direction.</returns>
    public static SortDirection DefaultDirectionFor(ApplicationSortColumn column) =>
        column is ApplicationSortColumn.SubmittedDate or ApplicationSortColumn.UpdatedDate or ApplicationSortColumn.ClaimedDate
            ? SortDirection.Descending
            : SortDirection.Ascending;

    /// <summary>
    /// Parses and validates raw list parameters. Missing values take their defaults; present but invalid values are
    /// errors (they are never silently replaced). Error field names are the query-parameter names.
    /// </summary>
    /// <param name="page">1-based page number; default 1.</param>
    /// <param name="pageSize">Rows per page, one of 10, 25, 50, 100; default 10.</param>
    /// <param name="sortBy">A key of <see cref="SortKeys"/>; default "submittedDate".</param>
    /// <param name="sortDirection">"asc" or "desc" (also "ascending"/"descending"); default depends on the column.</param>
    /// <param name="status">An <see cref="ApplicationStatus"/> name (case and spaces ignored, e.g. "Under Review").</param>
    /// <param name="propertyId">A property id (positive).</param>
    /// <returns>The query, or every problem found.</returns>
    public static OperationResult<ApplicationListQuery> Create(
        int? page = null,
        int? pageSize = null,
        string? sortBy = null,
        string? sortDirection = null,
        string? status = null,
        int? propertyId = null)
    {
        var errors = new List<FieldError>();

        if (page is < 1)
        {
            errors.Add(new FieldError("page", "The page number must be 1 or greater."));
        }

        if (pageSize is { } size && !AllowedPageSizes.Contains(size))
        {
            errors.Add(new FieldError("pageSize", $"The page size must be one of {string.Join(", ", AllowedPageSizes)}."));
        }

        var column = DefaultSortBy;
        var hasSort = !string.IsNullOrWhiteSpace(sortBy);
        if (hasSort && !SortKeys.TryGetValue(sortBy!.Trim(), out column))
        {
            errors.Add(new FieldError("sortBy", $"Unknown sort column. Use one of: {string.Join(", ", SortKeys.Keys)}."));
        }

        SortDirection? direction = null;
        if (!string.IsNullOrWhiteSpace(sortDirection))
        {
            direction = sortDirection.Trim().ToLowerInvariant() switch
            {
                "asc" or "ascending" => SortDirection.Ascending,
                "desc" or "descending" => SortDirection.Descending,
                _ => null,
            };
            if (direction is null)
            {
                errors.Add(new FieldError("sortDirection", "The sort direction must be \"asc\" or \"desc\"."));
            }
        }

        ApplicationStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            parsedStatus = ParseStatus(status);
            if (parsedStatus is null)
            {
                errors.Add(new FieldError("status",
                    $"Unknown status. Use one of: {string.Join(", ", Enum.GetNames<ApplicationStatus>())}."));
            }
        }

        if (propertyId is < 1)
        {
            errors.Add(new FieldError("propertyId", "The property id must be a positive number."));
        }

        if (errors.Count > 0)
        {
            return OperationResult<ApplicationListQuery>.From(OperationResult.Invalid(errors));
        }

        return OperationResult<ApplicationListQuery>.Success(new ApplicationListQuery
        {
            Page = page ?? DefaultPage,
            PageSize = pageSize ?? DefaultPageSize,
            SortBy = column,
            // Without a sort column the default sort keeps its own direction unless one was asked for explicitly.
            SortDirection = direction ?? (hasSort ? DefaultDirectionFor(column) : DefaultSortDirection),
            Status = parsedStatus,
            PropertyId = propertyId,
        });
    }

    /// <summary>
    /// Parses a status name: case-insensitive, ignoring spaces, hyphens and underscores (so "Under Review",
    /// "under-review" and "UnderReview" all match an <c>UnderReview</c> member). Numbers are rejected so only names
    /// that exist in the enum are accepted.
    /// </summary>
    /// <param name="value">The raw value.</param>
    /// <returns>The status, or null when it isn't a defined status name.</returns>
    public static ApplicationStatus? ParseStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var compact = string.Concat(value.Where(c => c is not (' ' or '-' or '_')));
        foreach (var candidate in Enum.GetValues<ApplicationStatus>())
        {
            if (string.Equals(candidate.ToString(), compact, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }
}
