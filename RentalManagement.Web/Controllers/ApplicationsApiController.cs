// =====================================================================================================================
// Module: RentalManagement.Web / Controllers / ApplicationsApiController.cs
// Purpose: JSON endpoint of the application grid: GET /api/applications/grid returns one page of the signed-in user's
//          application list (applicants: their own; property managers: all), filtered, sorted and paged in SQL by
//          ApplicationService. Parameters are validated by ApplicationListQuery (400 with problem details when
//          invalid). GET /api/applications/review-queue (property managers only) returns one page of the review queue
//          with each row's Claim / Review / Release URLs. Documented in the OpenAPI document (/openapi/v1.json in Development).
// =====================================================================================================================

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services.Applications;
using RentalManagement.Web.Infrastructure;
using RentalManagement.Web.ViewModels;

namespace RentalManagement.Web.Controllers;

/// <summary>
/// The application list as JSON, for the application grid. Authentication is the site's sign-in cookie; both roles may
/// call it, and the service restricts applicants to their own applications whatever the query string says.
/// </summary>
[ApiController]
[Route("api/applications")]
[Authorize(Roles = $"{Roles.Applicant},{Roles.PropertyManager}")]
[Tags("Applications")]
public sealed class ApplicationsApiController(ApplicationService applications, ReviewQueueService reviewQueue) : ControllerBase
{
    /// <summary>The grid endpoint's path, for callers that can't generate it from the route.</summary>
    public const string GridRoute = "/api/applications/grid";

    /// <summary>The review queue endpoint's path, for callers that can't generate it from the route.</summary>
    public const string ReviewQueueRoute = "/api/applications/review-queue";

    /// <summary>Gets one page of the signed-in user's application list.</summary>
    /// <remarks>
    /// Applicants receive only their own applications; property managers receive every application. Filters can only
    /// narrow that set. Filtering, counting, sorting and paging run in the database. Requesting a page past the last one
    /// returns an empty <c>items</c> array with the correct <c>totalCount</c>.
    ///
    /// Sort columns: <c>id</c>, <c>applicantName</c>, <c>propertyName</c>, <c>unitNumber</c>, <c>status</c>,
    /// <c>submittedDate</c>, <c>updatedDate</c>. Default: <c>submittedDate</c> descending (never-submitted drafts last),
    /// then by last update and id, so the order is stable across pages.
    /// </remarks>
    /// <param name="page">1-based page number. Default 1; must be 1 or greater.</param>
    /// <param name="pageSize">Rows per page: 10, 25, 50 or 100. Default 10.</param>
    /// <param name="sortBy">Sort column key (see the description). Default <c>submittedDate</c>.</param>
    /// <param name="sortDirection"><c>asc</c> or <c>desc</c>. Default: <c>desc</c> for dates, <c>asc</c> otherwise.</param>
    /// <param name="status">Only applications in this status, by name (e.g. <c>Submitted</c>; case and spaces ignored).</param>
    /// <param name="propertyId">Only applications for units of this property.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>The page of rows with paging and sort metadata.</returns>
    /// <response code="200">The requested page.</response>
    /// <response code="400">A parameter is invalid; the problem details list each one.</response>
    /// <response code="401">Not signed in (no valid authentication cookie).</response>
    /// <response code="403">Signed in without the Applicant or PropertyManager role.</response>
    [HttpGet("grid")]
    [EndpointName("GetApplicationGrid")]
    [EndpointSummary("Gets one page of the application list (filtered, sorted and paged on the server).")]
    [ProducesResponseType<ApplicationGridResponse>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(void))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(void))]
    public async Task<ActionResult<ApplicationGridResponse>> Grid(
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDirection,
        [FromQuery] string? status,
        [FromQuery] int? propertyId,
        CancellationToken cancellationToken)
    {
        var parsed = ApplicationListQuery.Create(page, pageSize, sortBy, sortDirection, status, propertyId);
        if (!parsed.Succeeded)
        {
            foreach (var error in parsed.Errors)
            {
                ModelState.AddModelError(error.Field ?? string.Empty, error.Message);
            }

            return ValidationProblem(statusCode: StatusCodes.Status400BadRequest, modelStateDictionary: ModelState);
        }

        var query = parsed.Value!;
        var isManager = User.IsInRole(Roles.PropertyManager);
        var result = await applications.ListPageAsync(User.GetRequiredUserId(), isManager, query, cancellationToken);

        return ApplicationGridResponse.From(result, query, isManager,
            id => Url.Action(nameof(ApplicationsController.Edit), "Applications", new { id })!);
    }

    /// <summary>Gets one page of the signed-in property manager's review queue.</summary>
    /// <remarks>
    /// <c>queue=available</c>: Submitted applications nobody has claimed (oldest submission first by default), each with
    /// a <c>claimUrl</c>. <c>queue=myClaims</c>: applications Under Review claimed by the signed-in manager (newest claim
    /// first by default), each with <c>reviewUrl</c> and <c>releaseUrl</c>. "My" is always the authenticated user; no
    /// user id is accepted. Property managers only: applicants get 403. Paging, sorting (the grid's columns plus
    /// <c>claimedDate</c>) and the property filter work as in the application grid and run in the database.
    /// </remarks>
    /// <param name="queue"><c>available</c> (default) or <c>myClaims</c>.</param>
    /// <param name="page">1-based page number. Default 1.</param>
    /// <param name="pageSize">Rows per page: 10, 25, 50 or 100. Default 10.</param>
    /// <param name="sortBy">Sort column key. Default <c>submittedDate</c> (available) or <c>claimedDate</c> (myClaims).</param>
    /// <param name="sortDirection"><c>asc</c> or <c>desc</c>. Default: the queue's default sort direction.</param>
    /// <param name="status">Narrows the queue further by status (rarely useful: each queue has one status).</param>
    /// <param name="propertyId">Only applications for units of this property.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>The page of rows with paging and sort metadata.</returns>
    /// <response code="200">The requested page.</response>
    /// <response code="400">A parameter is invalid; the problem details list each one.</response>
    /// <response code="401">Not signed in (no valid authentication cookie).</response>
    /// <response code="403">Signed in, but not a property manager.</response>
    [HttpGet("review-queue")]
    [Authorize(Roles = Roles.PropertyManager)]
    [EndpointName("GetReviewQueue")]
    [EndpointSummary("Gets one page of the property manager's review queue (available, or my claims).")]
    [ProducesResponseType<ApplicationGridResponse>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(void))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(void))]
    public async Task<ActionResult<ApplicationGridResponse>> ReviewQueue(
        [FromQuery] string? queue,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDirection,
        [FromQuery] string? status,
        [FromQuery] int? propertyId,
        CancellationToken cancellationToken)
    {
        var parsedQueue = string.IsNullOrWhiteSpace(queue) ? Core.Services.Applications.ReviewQueue.Available : ReviewQueueService.ParseQueue(queue);
        if (parsedQueue is not { } which)
        {
            ModelState.AddModelError("queue", $"Unknown queue. Use one of: {string.Join(", ", ReviewQueueService.QueueKeys.Keys)}.");
        }
        else if (string.IsNullOrWhiteSpace(sortBy))
        {
            // Each queue has its own natural order: first come, first served; my most recent claim first.
            var defaults = ReviewQueueService.DefaultQuery(which);
            sortBy = defaults.SortKey;
            sortDirection ??= defaults.SortDirectionKey;
        }

        var parsed = ApplicationListQuery.Create(page, pageSize, sortBy, sortDirection, status, propertyId);
        foreach (var error in parsed.Errors)
        {
            ModelState.AddModelError(error.Field ?? string.Empty, error.Message);
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(statusCode: StatusCodes.Status400BadRequest, modelStateDictionary: ModelState);
        }

        var query = parsed.Value!;
        var result = await reviewQueue.ListPageAsync(User.GetRequiredUserId(), parsedQueue!.Value, query, cancellationToken);
        if (!result.Succeeded)
        {
            // The role attribute already stops non-managers; the service's own check is the second line of defence.
            return Forbid();
        }

        return ApplicationGridResponse.From(result.Value!, query, isManager: true,
            id => Url.Action(nameof(ApplicationsController.Edit), "Applications", new { id })!,
            item => item.Status switch
            {
                ApplicationStatus.Submitted => new ReviewQueueActions(
                    Url.Action(nameof(ApplicationsController.Claim), "Applications", new { id = item.Id }), null, null),
                ApplicationStatus.UnderReview => new ReviewQueueActions(
                    null,
                    Url.Action(nameof(ApplicationsController.Review), "Applications", new { id = item.Id }),
                    Url.Action(nameof(ApplicationsController.Release), "Applications", new { id = item.Id })),
                _ => null,
            });
    }
}
