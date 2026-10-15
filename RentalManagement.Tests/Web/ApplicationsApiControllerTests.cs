// =====================================================================================================================
// Module: RentalManagement.Tests / Web / ApplicationsApiControllerTests.cs
// Purpose: Tests of GET /api/applications/grid (ApplicationsApiController) with the real ApplicationService on an EF
//          Core in-memory database: applicants only ever get their own rows (query parameters can't change that),
//          managers get everyone's with the applicant name, the response carries the paging/sort metadata, rows link to
//          the application page with a role-appropriate label, invalid parameters give 400 problem details naming each
//          parameter, and the endpoint's routing and authorization attributes.
// =====================================================================================================================

using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using RentalManagement.Core.Data;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services.Applications;
using RentalManagement.Web.Controllers;
using RentalManagement.Web.ViewModels;

namespace RentalManagement.Tests.Web;

/// <summary>
/// Tests of <see cref="ApplicationsApiController"/>. Data: Alice has a Draft and a Submitted application at Maple
/// Court; Bob has a Submitted application at Oak Row.
/// </summary>
public sealed class ApplicationsApiControllerTests : IDisposable
{
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Manager = "manager";
    private const string SecondManager = "manager2";

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly ApplicationDbContext _db;
    private readonly int _oakId;
    private readonly int _bobsApplicationId;

    /// <summary>Creates the per-test database.</summary>
    public ApplicationsApiControllerTests()
    {
        _db = NewContext();
        _db.Users.AddRange(
            new ApplicationUser { Id = Alice, UserName = "alice@test", FirstName = "Alice", LastName = "Archer" },
            new ApplicationUser { Id = Bob, UserName = "bob@test", FirstName = "Bob", LastName = "Baker" },
            new ApplicationUser { Id = Manager, UserName = "pm@test", FirstName = "Pat", LastName = "Manager" },
            new ApplicationUser { Id = SecondManager, UserName = "pm2@test", FirstName = "Sam", LastName = "Second" });
        var managerRole = new IdentityRole(Roles.PropertyManager) { Id = "role-pm" };
        _db.Roles.Add(managerRole);
        _db.UserRoles.AddRange(
            new IdentityUserRole<string> { UserId = Manager, RoleId = managerRole.Id },
            new IdentityUserRole<string> { UserId = SecondManager, RoleId = managerRole.Id });
        var type = new UnitType { Name = "Apartment", IsActive = true };
        var maple = NewProperty("Maple Court", type);
        var oak = NewProperty("Oak Row", type);
        _db.Properties.AddRange(maple, oak);
        _db.SaveChanges();
        _oakId = oak.Id;

        var now = _clock.GetUtcNow();
        var draft = RentalApplication.StartDraft(maple.Units.Single().Id, Alice, now);
        var submitted = RentalApplication.StartDraft(maple.Units.Single().Id, Alice, now);
        submitted.TransitionTo(ApplicationStatus.Submitted, Alice, now);
        var bobs = RentalApplication.StartDraft(oak.Units.Single().Id, Bob, now);
        bobs.TransitionTo(ApplicationStatus.Submitted, Bob, now);
        _db.RentalApplications.AddRange(draft, submitted, bobs);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        _bobsApplicationId = bobs.Id;
    }

    /// <summary>Disposes the test's DbContext.</summary>
    public void Dispose() => _db.Dispose();

    /// <summary>An applicant gets only their own rows and total, without the applicant-name field.</summary>
    [Fact]
    public async Task Applicant_gets_only_their_own_rows()
    {
        var response = await OkAsync(Alice, Roles.Applicant);

        Assert.Equal(2, response.TotalCount);
        Assert.Equal(2, response.Items.Count);
        Assert.All(response.Items, row => Assert.Null(row.ApplicantName));
        Assert.DoesNotContain(response.Items, row => row.Id == _bobsApplicationId);
    }

    /// <summary>
    /// Query parameters only narrow: asking for the property or status where only Bob has an application returns
    /// nothing to Alice (not Bob's row, not an error that would confirm it exists).
    /// </summary>
    [Fact]
    public async Task Applicant_cannot_reach_other_applicants_rows_through_parameters()
    {
        var byProperty = await OkAsync(Alice, Roles.Applicant, propertyId: _oakId);
        var byPropertyAndStatus = await OkAsync(Alice, Roles.Applicant, status: "Submitted", propertyId: _oakId);
        var sortedByApplicant = await OkAsync(Alice, Roles.Applicant, sortBy: "applicantName", pageSize: 100);

        Assert.Equal(0, byProperty.TotalCount);
        Assert.Empty(byPropertyAndStatus.Items);
        Assert.Equal(2, sortedByApplicant.TotalCount);
    }

    /// <summary>A manager gets every row, with applicant names, and the paging/sort metadata of the request.</summary>
    [Fact]
    public async Task Manager_gets_all_rows_with_metadata()
    {
        var response = await OkAsync(Manager, Roles.PropertyManager, page: 1, pageSize: 25, sortBy: "propertyName", sortDirection: "desc");

        Assert.Equal((1, 25, 3, 1, "propertyName", "desc"),
            (response.Page, response.PageSize, response.TotalCount, response.TotalPages, response.SortBy, response.SortDirection));
        Assert.Equal(["Oak Row", "Maple Court", "Maple Court"], response.Items.Select(r => r.PropertyName));
        Assert.Contains(response.Items, r => r.ApplicantName == "Bob Baker");
    }

    /// <summary>Filters are echoed back in the response, normalised (status by its enum name).</summary>
    [Fact]
    public async Task Filters_are_echoed_in_the_response()
    {
        var response = await OkAsync(Manager, Roles.PropertyManager, status: "submitted", propertyId: _oakId);

        Assert.Equal(("Submitted", (int?)_oakId), (response.Status, response.PropertyId));
        Assert.Equal(_bobsApplicationId, Assert.Single(response.Items).Id);
    }

    /// <summary>
    /// Each row links to the application page; the label hints at the next step for the role (Continue for the
    /// applicant's draft, Review for a manager on a submitted one, Open otherwise).
    /// </summary>
    [Fact]
    public async Task Rows_link_to_the_application_with_a_role_appropriate_label()
    {
        var alices = await OkAsync(Alice, Roles.Applicant, pageSize: 100);
        var managers = await OkAsync(Manager, Roles.PropertyManager, status: "Submitted", pageSize: 100);

        Assert.Equal(["Continue", "Open"], alices.Items.OrderBy(r => r.Status).Select(r => r.ActionLabel));
        Assert.All(managers.Items, r => Assert.Equal("Review", r.ActionLabel));
        Assert.All(alices.Items, r => Assert.Equal($"/Applications/Edit/{r.Id}", r.Url));
    }

    /// <summary>A page past the end is 200 with no rows and the real total.</summary>
    [Fact]
    public async Task Page_beyond_the_end_is_an_empty_page()
    {
        var response = await OkAsync(Manager, Roles.PropertyManager, page: 9);

        Assert.Empty(response.Items);
        Assert.Equal((9, 3, 1), (response.Page, response.TotalCount, response.TotalPages));
    }

    /// <summary>Invalid parameters give 400 with problem details that name each bad parameter.</summary>
    [Fact]
    public async Task Invalid_parameters_return_400_problem_details()
    {
        var result = await Controller(Alice, Roles.Applicant).Grid(0, 7, "password", "sideways", "Bogus", -1, default);

        var problem = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        var details = Assert.IsType<ValidationProblemDetails>(problem.Value);
        Assert.Equal(["page", "pageSize", "propertyId", "sortBy", "sortDirection", "status"], details.Errors.Keys.Order());
    }

    /// <summary>
    /// The JSON uses camelCase names, statuses as names, and leaves out the applicant name entirely for applicants.
    /// </summary>
    [Fact]
    public async Task Json_shape_matches_the_contract()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var forApplicant = JsonSerializer.SerializeToElement(await OkAsync(Alice, Roles.Applicant), options);
        var forManager = JsonSerializer.SerializeToElement(await OkAsync(Manager, Roles.PropertyManager), options);

        foreach (var name in new[] { "page", "pageSize", "totalCount", "totalPages", "sortBy", "sortDirection", "items" })
        {
            Assert.True(forApplicant.TryGetProperty(name, out _), name);
        }

        var applicantRow = forApplicant.GetProperty("items")[0];
        Assert.False(applicantRow.TryGetProperty("applicantName", out _));
        Assert.Equal(JsonValueKind.String, applicantRow.GetProperty("status").ValueKind);
        Assert.True(forManager.GetProperty("items")[0].TryGetProperty("applicantName", out _));
    }

    /// <summary>The endpoint is GET /api/applications/grid, for signed-in applicants and managers only.</summary>
    [Fact]
    public void Endpoint_is_a_GET_under_api_for_both_roles()
    {
        var type = typeof(ApplicationsApiController);
        var method = type.GetMethod(nameof(ApplicationsApiController.Grid))!;

        Assert.Equal("api/applications", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal("grid", method.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.NotNull(type.GetCustomAttribute<ApiControllerAttribute>());
        Assert.Equal($"{Roles.Applicant},{Roles.PropertyManager}", type.GetCustomAttribute<AuthorizeAttribute>()!.Roles);
        Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    // ---- Review queue (GET /api/applications/review-queue) ------------------------------------------------------

    /// <summary>Available lists the unclaimed Submitted applications, oldest first, each with only a Claim URL.</summary>
    [Fact]
    public async Task Review_queue_available_rows_offer_claim()
    {
        var response = await QueueOkAsync(Manager, "available");

        Assert.Equal(("submittedDate", "asc", 2), (response.SortBy, response.SortDirection, response.TotalCount));
        Assert.All(response.Items, row =>
        {
            Assert.Equal($"/Applications/Claim/{row.Id}", row.Actions!.ClaimUrl);
            Assert.Null(row.Actions.ReviewUrl);
            Assert.Null(row.Actions.ReleaseUrl);
            Assert.NotNull(row.ApplicantName);
        });
    }

    /// <summary>
    /// After a claim: the claimer's "my claims" has the row with Review and Release URLs and the claim owner; the other
    /// manager's "my claims" is empty; Available no longer lists it.
    /// </summary>
    [Fact]
    public async Task Review_queue_my_claims_is_per_manager_with_review_and_release()
    {
        await ClaimAsync(_bobsApplicationId, Manager);

        var mine = Assert.Single((await QueueOkAsync(Manager, "myClaims")).Items);
        var theirs = await QueueOkAsync(SecondManager, "myClaims");
        var available = await QueueOkAsync(SecondManager, "available");

        Assert.Equal((_bobsApplicationId, "UnderReview", "Under Review", "Pat Manager"), (mine.Id, mine.Status, mine.StatusLabel, mine.ClaimedByName));
        Assert.NotNull(mine.ClaimedDate);
        Assert.Equal(($"/Applications/Review/{mine.Id}", $"/Applications/Release/{mine.Id}", (string?)null),
            (mine.Actions!.ReviewUrl, mine.Actions.ReleaseUrl, mine.Actions.ClaimUrl));
        Assert.Equal(0, theirs.TotalCount);
        Assert.DoesNotContain(available.Items, r => r.Id == _bobsApplicationId);
    }

    /// <summary>
    /// An applicant never gets queue data: the action requires the PropertyManager role (stacked on the controller's
    /// roles), and even when called directly the service refuses and the action answers 403.
    /// </summary>
    [Fact]
    public async Task Review_queue_is_for_property_managers_only()
    {
        var method = typeof(ApplicationsApiController).GetMethod(nameof(ApplicationsApiController.ReviewQueue))!;
        Assert.Equal("review-queue", method.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal(Roles.PropertyManager, method.GetCustomAttribute<AuthorizeAttribute>()!.Roles);

        var result = await Controller(Alice, Roles.Applicant).ReviewQueue("available", null, null, null, null, null, null, default);

        Assert.IsType<ForbidResult>(result.Result);
    }

    /// <summary>An unknown queue name is a 400 that names the "queue" parameter.</summary>
    [Fact]
    public async Task Review_queue_rejects_an_unknown_queue()
    {
        var result = await Controller(Manager, Roles.PropertyManager).ReviewQueue("everything", null, null, null, null, null, null, default);

        var problem = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Contains("queue", Assert.IsType<ValidationProblemDetails>(problem.Value).Errors.Keys);
    }

    /// <summary>
    /// In the main grid managers see who holds a claim, but the claim owner and time never reach an applicant's JSON,
    /// even for their own application under review.
    /// </summary>
    [Fact]
    public async Task Claim_owner_is_shown_to_managers_only()
    {
        await ClaimAsync(_bobsApplicationId, Manager);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var bobs = JsonSerializer.SerializeToElement(await OkAsync(Bob, Roles.Applicant), options).GetProperty("items")[0];
        var managers = await OkAsync(Manager, Roles.PropertyManager, status: "UnderReview");

        Assert.Equal("Under Review", bobs.GetProperty("statusLabel").GetString());
        Assert.False(bobs.TryGetProperty("claimedByName", out _));
        Assert.False(bobs.TryGetProperty("claimedDate", out _));
        Assert.False(bobs.TryGetProperty("actions", out _));
        Assert.Equal("Pat Manager", Assert.Single(managers.Items).ClaimedByName);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>Claims an application through the real service.</summary>
    private async Task ClaimAsync(int id, string managerId)
    {
        await using var db = NewContext();
        var result = await new ApplicationReviewService(db, _clock).ClaimAsync(id, managerId);
        Assert.True(result.Succeeded, result.Error);
    }

    /// <summary>Calls the review-queue endpoint as a manager and asserts a 200 response.</summary>
    private async Task<ApplicationGridResponse> QueueOkAsync(string managerId, string queue)
    {
        var result = await Controller(managerId, Roles.PropertyManager).ReviewQueue(queue, null, null, null, null, null, null, default);
        Assert.Null(result.Result);
        return Assert.IsType<ApplicationGridResponse>(result.Value);
    }

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(_databaseName).Options);

    /// <summary>The controller as called by the given user, with a URL helper that builds application links.</summary>
    private ApplicationsApiController Controller(string userId, string role) =>
        new(new ApplicationService(NewContext(), _clock), new ReviewQueueService(NewContext()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, role)], "Test")),
                },
            },
            Url = new ApplicationLinkUrlHelper(),
        };

    /// <summary>Calls the endpoint and asserts a 200 response.</summary>
    private async Task<ApplicationGridResponse> OkAsync(
        string userId, string role, int? page = null, int? pageSize = null, string? sortBy = null, string? sortDirection = null,
        string? status = null, int? propertyId = null)
    {
        var result = await Controller(userId, role).Grid(page, pageSize, sortBy, sortDirection, status, propertyId, default);
        Assert.Null(result.Result);
        return Assert.IsType<ApplicationGridResponse>(result.Value);
    }

    private static Property NewProperty(string name, UnitType type)
    {
        var property = new Property
        {
            Name = name,
            Address = new Address { Street = "1 Main St", City = "Troy", State = "NY", PostalCode = "12180" },
        };
        property.Units.Add(new Unit { UnitNumber = "101", Bedrooms = 1, MonthlyRent = 1000m, UnitType = type });
        return property;
    }

    /// <summary>Builds "/{controller}/{action}/{id}" links without the routing system.</summary>
    private sealed class ApplicationLinkUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();

        public string? Action(UrlActionContext actionContext) =>
            $"/{actionContext.Controller}/{actionContext.Action}/{new RouteValueDictionary(actionContext.Values)["id"]}";

        public string? Content(string? contentPath) => contentPath;

        public bool IsLocalUrl(string? url) => true;

        public string? Link(string? routeName, object? values) => null;

        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }
}
