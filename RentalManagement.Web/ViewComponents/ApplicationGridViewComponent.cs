// =====================================================================================================================
// Module: RentalManagement.Web / ViewComponents / ApplicationGridViewComponent.cs
// Purpose: The reusable application grid: filters (status, property), page-size selector, sortable column headers,
//          pager, result summary, and loading / empty / error states. It decides the columns for the viewer's role (or
//          for a review queue) and loads the property filter's options itself; the rows are loaded and refreshed by
//          wwwroot/js/application-grid.js from GET /api/applications/grid, or /api/applications/review-queue for the
//          review queue. Used by Applications/Index (full list), Home/Index (a compact grid) and Applications/ReviewQueue.
// =====================================================================================================================

using Microsoft.AspNetCore.Mvc;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services.Applications;
using RentalManagement.Core.Services.Properties;
using RentalManagement.Web.Controllers;
using RentalManagement.Web.Infrastructure;
using RentalManagement.Web.ViewModels;

namespace RentalManagement.Web.ViewComponents;

/// <summary>
/// Renders an application grid for the signed-in user. Any view can host one (several per page with distinct
/// <see cref="ApplicationGridOptions.Id"/>s). The JSON endpoint applies the same per-user restriction as the rest of
/// the app, so a host can only narrow what the grid shows. A review-queue grid renders only for property managers.
/// </summary>
public sealed class ApplicationGridViewComponent(ApplicationService applications) : ViewComponent
{
    /// <summary>Builds the grid shell for the viewer's role.</summary>
    /// <param name="options">The host's options; null for the defaults (full list, filters shown).</param>
    /// <returns>The rendered grid, or nothing for a review queue viewed by someone who isn't a property manager.</returns>
    public async Task<IViewComponentResult> InvokeAsync(ApplicationGridOptions? options = null)
    {
        options ??= new ApplicationGridOptions();
        var isManager = UserClaimsPrincipal.IsInRole(Roles.PropertyManager);
        var userId = UserClaimsPrincipal.GetRequiredUserId();
        if (options.Queue is not null && !isManager)
        {
            return Content(string.Empty);
        }

        IReadOnlyList<PropertyOption> propertyOptions = [];
        if (options.ShowFilters)
        {
            propertyOptions = await applications.GetListPropertyOptionsAsync(userId, isManager, HttpContext.RequestAborted);
        }

        var apiUrl = options.Queue is null
            ? Url.Action(nameof(ApplicationsApiController.Grid), "ApplicationsApi") ?? ApplicationsApiController.GridRoute
            : Url.Action(nameof(ApplicationsApiController.ReviewQueue), "ApplicationsApi") ?? ApplicationsApiController.ReviewQueueRoute;
        var columns = options.Queue is { } queue ? QueueColumnsFor(queue) : ColumnsFor(isManager);
        return View(new ApplicationGridModel(options, isManager, apiUrl, columns, propertyOptions));
    }

    /// <summary>
    /// The grid's columns. Secondary columns are hidden on narrow screens (their values appear under the property in
    /// the first column), so the table fits a 375 px phone without sideways scrolling.
    /// </summary>
    /// <param name="isManager">Whether to include the applicant column.</param>
    /// <returns>The columns in display order.</returns>
    internal static IReadOnlyList<ApplicationGridColumn> ColumnsFor(bool isManager)
    {
        var columns = new List<ApplicationGridColumn>
        {
            Column(ApplicationSortColumn.Id, "#", "d-none d-lg-table-cell"),
            Column(ApplicationSortColumn.PropertyName, "Property", ""),
            Column(ApplicationSortColumn.UnitNumber, "Unit", "d-none d-md-table-cell"),
        };
        if (isManager)
        {
            columns.Add(Column(ApplicationSortColumn.ApplicantName, "Applicant", "d-none d-sm-table-cell"));
        }

        columns.Add(Column(ApplicationSortColumn.Status, "Status", ""));
        columns.Add(Column(ApplicationSortColumn.SubmittedDate, "Submitted", "d-none d-md-table-cell"));
        columns.Add(Column(ApplicationSortColumn.UpdatedDate, "Last updated", "d-none d-lg-table-cell"));
        columns.Add(new ApplicationGridColumn("action", "Open", "text-end", Sortable: false, DefaultDirection: "asc"));
        return columns;
    }

    /// <summary>
    /// A review queue's columns: applicant, property, unit, status and submission date, the claim time for the
    /// manager's own claims, and the row actions (Claim, or Review and Release).
    /// </summary>
    /// <param name="queue">The queue.</param>
    /// <returns>The columns in display order.</returns>
    internal static IReadOnlyList<ApplicationGridColumn> QueueColumnsFor(ReviewQueue queue)
    {
        var columns = new List<ApplicationGridColumn>
        {
            Column(ApplicationSortColumn.Id, "#", "d-none d-lg-table-cell"),
            Column(ApplicationSortColumn.PropertyName, "Property", ""),
            Column(ApplicationSortColumn.UnitNumber, "Unit", "d-none d-md-table-cell"),
            Column(ApplicationSortColumn.ApplicantName, "Applicant", "d-none d-sm-table-cell"),
            Column(ApplicationSortColumn.Status, "Status", "d-none d-sm-table-cell"),
            Column(ApplicationSortColumn.SubmittedDate, "Submitted", "d-none d-md-table-cell"),
        };
        if (queue == ReviewQueue.MyClaims)
        {
            columns.Add(Column(ApplicationSortColumn.ClaimedDate, "Claimed", "d-none d-lg-table-cell"));
        }

        columns.Add(new ApplicationGridColumn("queueActions", "Actions", "text-end", Sortable: false, DefaultDirection: "asc"));
        return columns;
    }

    private static ApplicationGridColumn Column(ApplicationSortColumn column, string label, string css) =>
        new(ApplicationListQuery.SortKeyOf(column), label, css, Sortable: true,
            ApplicationListQuery.DefaultDirectionFor(column) == SortDirection.Ascending ? "asc" : "desc");
}
