// =====================================================================================================================
// Module: RentalManagement.Core / Services / Applications / ReviewQueueService.cs
// Purpose: The property manager's review queue: one page of either "Available for review" (Submitted, unclaimed) or
//          "My claimed applications" (Under Review, claimed by the signed-in manager), narrowed by the usual status and
//          property filters and sorted and paged in SQL with the same code as the application list. Refuses callers who
//          are not property managers, whatever the controller checked. Claiming and releasing are in
//          ApplicationReviewService.
// =====================================================================================================================

using Microsoft.EntityFrameworkCore;
using RentalManagement.Core.Data;
using RentalManagement.Core.Domain;

namespace RentalManagement.Core.Services.Applications;

/// <summary>
/// Reads the review queue. The queue condition is applied first and unconditionally (as the applicant restriction is
/// in the application list), so a status or property filter can only narrow it; "my claims" is always the signed-in
/// manager's own, never a user id from the request.
/// </summary>
public sealed class ReviewQueueService(ApplicationDbContext db)
{
    /// <summary>The public names of the queues (query string and JSON), matched case-insensitively. Any other value is rejected.</summary>
    public static IReadOnlyDictionary<string, ReviewQueue> QueueKeys { get; } =
        new Dictionary<string, ReviewQueue>(StringComparer.OrdinalIgnoreCase)
        {
            ["available"] = ReviewQueue.Available,
            ["myClaims"] = ReviewQueue.MyClaims,
        };

    /// <summary>The public name of a queue, e.g. "myClaims".</summary>
    /// <param name="queue">The queue.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(ReviewQueue queue) => QueueKeys.First(k => k.Value == queue).Key;

    /// <summary>Parses a queue name; null when it isn't one of <see cref="QueueKeys"/>.</summary>
    /// <param name="value">The raw value.</param>
    /// <returns>The queue, or null.</returns>
    public static ReviewQueue? ParseQueue(string? value) =>
        value is not null && QueueKeys.TryGetValue(value.Trim(), out var queue) ? queue : null;

    /// <summary>The default sort per queue: oldest submission first (first come, first served), newest claim first.</summary>
    /// <param name="queue">The queue.</param>
    /// <returns>The default query (page 1, 10 rows).</returns>
    public static ApplicationListQuery DefaultQuery(ReviewQueue queue) => queue == ReviewQueue.Available
        ? ApplicationListQuery.Create(sortBy: "submittedDate", sortDirection: "asc").Value!
        : ApplicationListQuery.Create(sortBy: "claimedDate", sortDirection: "desc").Value!;

    /// <summary>One page of a review queue for the signed-in property manager.</summary>
    /// <param name="managerId">The signed-in user; must be a property manager.</param>
    /// <param name="queue">Available (Submitted, unclaimed) or MyClaims (Under Review, claimed by this manager).</param>
    /// <param name="query">The validated page, sort and filter request.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>The page, or NotFound when the caller is not a property manager.</returns>
    public async Task<OperationResult<PagedResult<ApplicationListItem>>> ListPageAsync(
        string managerId, ReviewQueue queue, ApplicationListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!await ApplicationReviewService.IsPropertyManagerAsync(db, managerId, cancellationToken))
        {
            return OperationResult<PagedResult<ApplicationListItem>>.From(OperationResult.NotFound("Review queue not found."));
        }

        var filtered = Filter(managerId, queue, query.Filter);
        var totalCount = await filtered.CountAsync(cancellationToken);
        if (query.Skip >= totalCount)
        {
            return OperationResult<PagedResult<ApplicationListItem>>.Success(
                new PagedResult<ApplicationListItem>([], query.Page, query.PageSize, totalCount));
        }

        var items = await ApplicationService.Page(filtered, query).ToListAsync(cancellationToken);
        return OperationResult<PagedResult<ApplicationListItem>>.Success(
            new PagedResult<ApplicationListItem>(items, query.Page, query.PageSize, totalCount));
    }

    /// <summary>
    /// The queue's conditions, composed on <see cref="IQueryable"/> so they run in SQL. Internal so tests can read the
    /// generated WHERE clause.
    /// </summary>
    /// <param name="managerId">The signed-in property manager.</param>
    /// <param name="queue">The queue.</param>
    /// <param name="filter">Further status/property criteria (narrowing only).</param>
    /// <returns>The filtered, untracked applications.</returns>
    internal IQueryable<RentalApplication> Filter(string managerId, ReviewQueue queue, ApplicationListFilter filter)
    {
        var applications = db.RentalApplications.AsNoTracking();
        applications = queue switch
        {
            ReviewQueue.Available => applications.Where(a => a.Status == ApplicationStatus.Submitted && a.ClaimedByUserId == null),
            ReviewQueue.MyClaims => applications.Where(a => a.Status == ApplicationStatus.UnderReview && a.ClaimedByUserId == managerId),
            _ => throw new ArgumentOutOfRangeException(nameof(queue), queue, null),
        };
        return ApplicationService.ApplyFilter(applications, filter);
    }
}
