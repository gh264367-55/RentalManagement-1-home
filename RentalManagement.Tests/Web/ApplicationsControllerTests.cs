// =====================================================================================================================
// Module: RentalManagement.Tests / Web / ApplicationsControllerTests.cs
// Purpose: Tests of ApplicationsController with the real Core services on an EF Core in-memory database and a fake
//          clock: the single form's command dispatch (continue / back / submit), that invalid input re-renders the
//          section without saving and with field-specific errors, that Back never saves, the controller-level
//          rejections (someone else's application, a non-editable one, bad commands), the list page's grid parameters,
//          the review modal's 422 / JSON responses (claim owner only), and Claim / Release / the review queue page.
// =====================================================================================================================

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using RentalManagement.Core.Data;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services.Applications;
using RentalManagement.Tests.Services;
using RentalManagement.Web.Controllers;
using RentalManagement.Web.ViewModels;

namespace RentalManagement.Tests.Web;

/// <summary>
/// Tests of <see cref="ApplicationsController"/>. Each test gets a fresh database with applicants Alice and Bob, a
/// property manager, and two units at different properties.
/// </summary>
public sealed class ApplicationsControllerTests : IDisposable
{
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Manager = "manager";
    private const string SecondManager = "manager2";

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly ApplicationDbContext _db;
    private readonly int _unitId;
    private readonly int _otherUnitId;
    private readonly int _otherPropertyId;

    /// <summary>Creates the per-test database with roles, users and two units at two properties.</summary>
    public ApplicationsControllerTests()
    {
        _clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        _db = NewContext();

        var managerRole = new IdentityRole(Roles.PropertyManager) { Id = "role-pm" };
        _db.Roles.Add(managerRole);
        _db.Users.AddRange(
            new ApplicationUser { Id = Alice, UserName = "alice@test", Email = "alice@test", FirstName = "Alice", LastName = "Archer", PhoneNumber = "(518) 555-0101" },
            new ApplicationUser { Id = Bob, UserName = "bob@test", Email = "bob@test", FirstName = "Bob", LastName = "Baker" },
            new ApplicationUser { Id = Manager, UserName = "pm@test", Email = "pm@test", FirstName = "Pat", LastName = "Manager" },
            new ApplicationUser { Id = SecondManager, UserName = "pm2@test", Email = "pm2@test", FirstName = "Sam", LastName = "Second" });
        _db.UserRoles.AddRange(
            new IdentityUserRole<string> { UserId = Manager, RoleId = managerRole.Id },
            new IdentityUserRole<string> { UserId = SecondManager, RoleId = managerRole.Id });

        var type = new UnitType { Name = "Apartment", IsActive = true };
        var maple = NewProperty("Maple Court", type);
        var oak = NewProperty("Oak Row", type);
        _db.Properties.AddRange(maple, oak);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        (_unitId, _otherUnitId, _otherPropertyId) = (maple.Units.Single().Id, oak.Units.Single().Id, oak.Id);
    }

    /// <summary>Disposes the test's DbContext.</summary>
    public void Dispose() => _db.Dispose();

    // ---- The one form: continue / back / submit -----------------------------------------------------------------

    /// <summary>Back goes to the previous section and saves nothing, even with changed fields in the post.</summary>
    [Fact]
    public async Task Back_moves_to_the_previous_section_without_saving()
    {
        var id = await StartAsync(Alice, _unitId);
        var before = await SnapshotAsync(id);
        var posted = Page(ApplicationSection.ResidenceHistory, ApplicationRulesTests.ValidInfo with { FirstName = "Changed" });

        var result = await Controller(Alice, Roles.Applicant).Edit(id, posted, ApplicationsController.CommandBack, default);

        AssertRedirectToSection(result, ApplicationSection.ApplicantInfo);
        Assert.Equal(before, await SnapshotAsync(id));
    }

    /// <summary>Back on the first section stays on it.</summary>
    [Fact]
    public async Task Back_on_the_first_section_stays_there()
    {
        var id = await StartAsync(Alice, _unitId);

        var result = await Controller(Alice, Roles.Applicant).Edit(id, Page(ApplicationSection.ApplicantInfo), ApplicationsController.CommandBack, default);

        AssertRedirectToSection(result, ApplicationSection.ApplicantInfo);
    }

    /// <summary>
    /// Continue with data the business rules reject re-renders the same section with the posted values and the
    /// error on the right field ("ApplicantInfo.Phone"), and saves nothing.
    /// </summary>
    [Fact]
    public async Task Continue_with_invalid_data_rerenders_the_section_with_field_errors_and_saves_nothing()
    {
        var id = await StartAsync(Alice, _unitId);
        var before = await SnapshotAsync(id);
        var controller = Controller(Alice, Roles.Applicant);

        var result = await controller.Edit(id, Page(ApplicationSection.ApplicantInfo, ApplicationRulesTests.ValidInfo with { Phone = "123" }),
            ApplicationsController.CommandContinue, default);

        var view = Assert.IsType<ViewResult>(result);
        var page = Assert.IsType<ApplicationPageViewModel>(view.Model);
        Assert.Equal(ApplicationSection.ApplicantInfo, page.CurrentSection);
        Assert.Equal("123", page.ApplicantInfo.Phone);
        Assert.True(page.ApplicantInfo.IsEditable);
        Assert.True(controller.ModelState.ContainsKey("ApplicantInfo.Phone"));
        Assert.Equal(before, await SnapshotAsync(id));
    }

    /// <summary>When MVC validation already failed, the service isn't called and nothing is saved.</summary>
    [Fact]
    public async Task Continue_with_model_validation_errors_does_not_call_the_service()
    {
        var id = await StartAsync(Alice, _unitId);
        var controller = Controller(Alice, Roles.Applicant);
        controller.ModelState.AddModelError("ApplicantInfo.City", "The City field is required.");

        var result = await controller.Edit(id, Page(ApplicationSection.ApplicantInfo, ApplicationRulesTests.ValidInfo), ApplicationsController.CommandContinue, default);

        Assert.IsType<ViewResult>(result);
        Assert.False((await LoadAsync(id)).ApplicantInfoCompleted);
    }

    /// <summary>Continue with valid data saves Section 1 and redirects to Residence history.</summary>
    [Fact]
    public async Task Continue_with_valid_data_saves_and_moves_on()
    {
        var id = await StartAsync(Alice, _unitId);

        var result = await Controller(Alice, Roles.Applicant).Edit(id, Page(ApplicationSection.ApplicantInfo, ApplicationRulesTests.ValidInfo),
            ApplicationsController.CommandContinue, default);

        AssertRedirectToSection(result, ApplicationSection.ResidenceHistory);
        Assert.True((await LoadAsync(id)).ApplicantInfoCompleted);
    }

    /// <summary>Continue on Residence history without residences re-renders it with a section-level message.</summary>
    [Fact]
    public async Task Continue_on_residence_history_without_residences_shows_a_section_error()
    {
        var id = await StartAsync(Alice, _unitId);
        var controller = Controller(Alice, Roles.Applicant);

        var result = await controller.Edit(id, Page(ApplicationSection.ResidenceHistory), ApplicationsController.CommandContinue, default);

        Assert.IsType<ViewResult>(result);
        Assert.Contains("at least one prior residence", controller.ModelState[string.Empty]!.Errors.Single().ErrorMessage);
        Assert.False((await LoadAsync(id)).ResidenceHistoryCompleted);
    }

    /// <summary>Submit from the Summary submits a complete draft and sets a success message for the next page.</summary>
    [Fact]
    public async Task Submit_from_the_summary_submits_and_confirms()
    {
        var id = await CompleteAsync(Alice, _unitId);
        var controller = Controller(Alice, Roles.Applicant);

        var result = await controller.Edit(id, Page(ApplicationSection.Summary), ApplicationsController.CommandSubmit, default);

        AssertRedirectToSection(result, ApplicationSection.Summary);
        Assert.Equal(ApplicationStatus.Submitted, (await LoadAsync(id)).Status);
        Assert.Contains("submitted", (string)controller.TempData["Alert.Success"]!);
    }

    /// <summary>Submit is only accepted on the Summary, and an unknown command is a bad request.</summary>
    [Theory]
    [InlineData(ApplicationSection.ApplicantInfo, "submit")]
    [InlineData(ApplicationSection.ResidenceHistory, "submit")]
    [InlineData(ApplicationSection.Summary, "approve")]
    [InlineData(ApplicationSection.Summary, null)]
    public async Task Misplaced_or_unknown_commands_are_bad_requests(ApplicationSection section, string? command)
    {
        var id = await CompleteAsync(Alice, _unitId);

        var result = await Controller(Alice, Roles.Applicant).Edit(id, Page(section), command, default);

        Assert.IsType<BadRequestResult>(result);
        Assert.Equal(ApplicationStatus.Draft, (await LoadAsync(id)).Status);
    }

    // ---- Rejections ---------------------------------------------------------------------------------------------

    /// <summary>Another applicant's application is "not found" for viewing and for every command, and stays unchanged.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("continue")]
    [InlineData("back")]
    [InlineData("submit")]
    public async Task Another_applicants_application_is_not_found(string? command)
    {
        var bobs = await CompleteAsync(Bob, _unitId);
        var before = await SnapshotAsync(bobs);
        var controller = Controller(Alice, Roles.Applicant);

        var result = command is null
            ? await controller.Edit(bobs, ApplicationSection.Summary, default)
            : await controller.Edit(bobs, Page(ApplicationSection.Summary, ApplicationRulesTests.ValidInfo), command, default);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(before, await SnapshotAsync(bobs));
    }

    /// <summary>A post to the owner's own submitted application is rejected with 409 and the page shown read-only.</summary>
    [Fact]
    public async Task Posts_to_a_non_editable_application_are_rejected_with_409()
    {
        var id = await CompleteAsync(Alice, _unitId);
        await Controller(Alice, Roles.Applicant).Edit(id, Page(ApplicationSection.Summary), ApplicationsController.CommandSubmit, default);
        var controller = Controller(Alice, Roles.Applicant);

        var result = await controller.Edit(id, Page(ApplicationSection.ApplicantInfo, ApplicationRulesTests.ValidInfo with { FirstName = "Late" }),
            ApplicationsController.CommandContinue, default);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, controller.Response.StatusCode);
        Assert.False(((ApplicationPageViewModel)view.Model!).IsEditable);
        Assert.NotEqual("Late", (await LoadAsync(id)).FirstName);
    }

    /// <summary>A property manager can open any application, always read-only.</summary>
    [Fact]
    public async Task Managers_see_any_application_read_only()
    {
        var id = await CompleteAsync(Alice, _unitId);

        var result = await Controller(Manager, Roles.PropertyManager).Edit(id, ApplicationSection.Summary, default);

        var page = Assert.IsType<ApplicationPageViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.False(page.IsEditable);
        Assert.True(page.Header.IsManager);
    }

    // ---- List ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The list page hands the validated query-string parameters to the grid as its first state, and keeps the address
    /// in sync. Which rows are shown is decided by the JSON endpoint (see ApplicationsApiControllerTests).
    /// </summary>
    [Fact]
    public void List_passes_valid_parameters_to_the_grid()
    {
        var model = Index(Manager, Roles.PropertyManager, new ApplicationListViewModel
        {
            Status = "Returned", PropertyId = _otherPropertyId, Page = 3, PageSize = 25, SortBy = "unitNumber", SortDirection = "desc",
        });

        var query = model.Grid.Query;
        Assert.Equal((ApplicationStatus.Returned, _otherPropertyId, 3, 25, ApplicationSortColumn.UnitNumber, SortDirection.Descending),
            (query.Status, query.PropertyId, query.Page, query.PageSize, query.SortBy, query.SortDirection));
        Assert.True(model.Grid.SyncUrl);
        Assert.Null(model.Grid.Notice);
        Assert.True(model.IsManager);
    }

    /// <summary>
    /// Invalid parameters (a value the service rejects, or a model-binding error) don't fail the page: the grid starts
    /// from the defaults and a notice says so.
    /// </summary>
    [Fact]
    public void Invalid_list_parameters_fall_back_to_the_default_list()
    {
        var rejected = Index(Alice, Roles.Applicant, new ApplicationListViewModel { Status = "Bogus", PageSize = 7 });

        var controller = Controller(Alice, Roles.Applicant);
        controller.ModelState.AddModelError(nameof(ApplicationListViewModel.Page), "The value 'abc' is not valid.");
        var unbindable = Assert.IsType<ApplicationListViewModel>(
            Assert.IsType<ViewResult>(controller.Index(new ApplicationListViewModel { Status = "Draft" })).Model);

        Assert.All(new[] { rejected, unbindable }, model =>
        {
            Assert.Same(ApplicationListQuery.Default, model.Grid.Query);
            Assert.NotNull(model.Grid.Notice);
        });
        Assert.False(rejected.IsManager);
    }

    // ---- Review modal -------------------------------------------------------------------------------------------

    /// <summary>A Return without a comment re-renders the review modal with HTTP 422 and the error on "Comment".</summary>
    [Fact]
    public async Task Invalid_review_rerenders_the_modal_with_422_and_a_field_error()
    {
        var id = await ClaimedAsync(Alice, _unitId, Manager);
        var controller = Controller(Manager, Roles.PropertyManager);

        var result = await controller.Review(id, new ReviewFormViewModel { Outcome = ReviewOutcome.Return }, default);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("_ReviewForm", partial.ViewName);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, controller.Response.StatusCode);
        Assert.True(controller.ModelState.ContainsKey(nameof(ReviewFormViewModel.Comment)));
        Assert.Equal(ApplicationStatus.UnderReview, (await LoadAsync(id)).Status);
    }

    /// <summary>A valid review by the claim owner returns the JSON success signal (close the modal, refresh the regions).</summary>
    [Fact]
    public async Task Valid_review_returns_json_success()
    {
        var id = await ClaimedAsync(Alice, _unitId, Manager);

        var result = await Controller(Manager, Roles.PropertyManager)
            .Review(id, new ReviewFormViewModel { Outcome = ReviewOutcome.Deny, Comment = "Incomplete references." }, default);

        Assert.IsType<JsonResult>(result);
        Assert.Equal(ApplicationStatus.Denied, (await LoadAsync(id)).Status);
    }

    /// <summary>
    /// The review modal opens only for the claim owner. A draft, an unclaimed Submitted application and someone else's
    /// claim each get a 409 explanation instead of the form.
    /// </summary>
    [Fact]
    public async Task Review_modal_opens_only_for_the_claim_owner()
    {
        var draft = await StartAsync(Alice, _unitId);
        var unclaimed = await SubmittedAsync(Bob, _otherUnitId);
        var claimed = await ClaimedAsync(Bob, _unitId, Manager);

        foreach (var (id, expected) in new[] { (draft, "draft"), (unclaimed, "Claim this application"), (claimed, "Pat Manager") })
        {
            var controller = Controller(SecondManager, Roles.PropertyManager);
            var result = await controller.Review(id, default);

            var partial = Assert.IsType<PartialViewResult>(result);
            Assert.Equal("_ModalMessage", partial.ViewName);
            Assert.Contains(expected, Assert.IsType<string>(partial.Model));
            Assert.Equal(StatusCodes.Status409Conflict, controller.Response.StatusCode);
        }

        var owner = await Controller(Manager, Roles.PropertyManager).Review(claimed, default);
        Assert.Equal("_ReviewForm", Assert.IsType<PartialViewResult>(owner).ViewName);
    }

    /// <summary>
    /// A direct POST by another manager can't complete a review claimed by someone else: the modal re-renders with
    /// the reason (422) and nothing changes.
    /// </summary>
    [Fact]
    public async Task Review_post_by_another_manager_is_refused()
    {
        var id = await ClaimedAsync(Alice, _unitId, Manager);
        var controller = Controller(SecondManager, Roles.PropertyManager);

        var result = await controller.Review(id, new ReviewFormViewModel { Outcome = ReviewOutcome.Deny, Comment = "No." }, default);

        Assert.Equal("_ReviewForm", Assert.IsType<PartialViewResult>(result).ViewName);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, controller.Response.StatusCode);
        Assert.Equal((ApplicationStatus.UnderReview, Manager), ((await LoadAsync(id)).Status, (await LoadAsync(id)).ClaimedByUserId));
    }

    // ---- Claim, release and the queue page ---------------------------------------------------------------------------

    /// <summary>A fetch-posted claim returns JSON success and the application is Under Review by the signed-in manager.</summary>
    [Fact]
    public async Task Claim_from_script_returns_json_success()
    {
        var id = await SubmittedAsync(Alice, _unitId);

        var result = await Controller(Manager, Roles.PropertyManager, ajax: true).Claim(id, returnUrl: null, default);

        Assert.IsType<JsonResult>(result);
        Assert.Equal((ApplicationStatus.UnderReview, Manager), ((await LoadAsync(id)).Status, (await LoadAsync(id)).ClaimedByUserId));
    }

    /// <summary>
    /// A claim that lost to another manager answers 409 with a JSON message naming the claim owner, and never
    /// overwrites the existing claim.
    /// </summary>
    [Fact]
    public async Task Claim_conflict_returns_409_json_and_keeps_the_existing_claim()
    {
        var id = await ClaimedAsync(Alice, _unitId, Manager);
        var controller = Controller(SecondManager, Roles.PropertyManager, ajax: true);

        var result = await controller.Claim(id, returnUrl: null, default);

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, controller.Response.StatusCode);
        Assert.Contains("Pat Manager", json.Value!.ToString());
        Assert.Equal(Manager, (await LoadAsync(id)).ClaimedByUserId);
    }

    /// <summary>Without script the claim redirects back to the application with a one-time message.</summary>
    [Fact]
    public async Task Claim_without_script_redirects_with_a_message()
    {
        var id = await SubmittedAsync(Alice, _unitId);
        var controller = Controller(Manager, Roles.PropertyManager);

        var result = await controller.Claim(id, returnUrl: null, default);

        Assert.Equal(nameof(ApplicationsController.Edit), Assert.IsType<RedirectToActionResult>(result).ActionName);
        Assert.NotNull(controller.TempData[RentalManagement.Web.Infrastructure.TempDataKeys.Success]);
    }

    /// <summary>
    /// The release dialog offers the confirm button only to the claim owner; another manager gets the reason (422).
    /// The owner's confirmed release returns JSON and puts the application back to Submitted.
    /// </summary>
    [Fact]
    public async Task Only_the_claim_owner_can_release()
    {
        var id = await ClaimedAsync(Alice, _unitId, Manager);

        var other = Controller(SecondManager, Roles.PropertyManager);
        var refused = await other.Release(id, default);
        Assert.Equal("_Confirm", Assert.IsType<PartialViewResult>(refused).ViewName);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, other.Response.StatusCode);
        var otherPost = Controller(SecondManager, Roles.PropertyManager);
        Assert.IsType<PartialViewResult>(await otherPost.ReleaseConfirmed(id, default));
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, otherPost.Response.StatusCode);
        Assert.Equal(ApplicationStatus.UnderReview, (await LoadAsync(id)).Status);

        var owner = Controller(Manager, Roles.PropertyManager);
        Assert.Equal("_Confirm", Assert.IsType<PartialViewResult>(await owner.Release(id, default)).ViewName);
        Assert.Equal(StatusCodes.Status200OK, owner.Response.StatusCode);
        Assert.IsType<JsonResult>(await Controller(Manager, Roles.PropertyManager).ReleaseConfirmed(id, default));
        var released = await LoadAsync(id);
        Assert.Equal((ApplicationStatus.Submitted, (string?)null), (released.Status, released.ClaimedByUserId));
    }

    /// <summary>The queue page shows the two queue grids, each bound to its queue with the status filter hidden.</summary>
    [Fact]
    public void Review_queue_page_has_both_queue_grids()
    {
        var model = Assert.IsType<ReviewQueueViewModel>(
            Assert.IsType<ViewResult>(Controller(Manager, Roles.PropertyManager).ReviewQueue()).Model);

        Assert.Equal((ReviewQueue.Available, false), (model.Available.Queue, model.Available.ShowStatusFilter));
        Assert.Equal((ReviewQueue.MyClaims, false), (model.MyClaims.Queue, model.MyClaims.ShowStatusFilter));
        Assert.NotEqual(model.Available.Id, model.MyClaims.Id);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>A context on this test's in-memory database.</summary>
    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(_databaseName).Options);

    /// <summary>
    /// A controller wired like a request from the given user: real services on a fresh context, a principal with the
    /// user id and role claims, and TempData.
    /// </summary>
    private ApplicationsController Controller(string userId, string role, bool ajax = false)
    {
        var db = NewContext();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, role)], "Test")),
        };
        if (ajax)
        {
            httpContext.Request.Headers.XRequestedWith = "XMLHttpRequest";
        }

        return new ApplicationsController(new ApplicationService(db, _clock), new ApplicationReviewService(db, _clock), _clock)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new InMemoryTempDataProvider()),
            Url = new StubUrlHelper(),
        };
    }

    /// <summary>A posted page model for a section, optionally with Section 1 values.</summary>
    private static ApplicationPageViewModel Page(ApplicationSection section, ApplicantInfoInput? info = null) => new()
    {
        CurrentSection = section,
        ApplicantInfo = info is null ? new ApplicantInfoViewModel() : ApplicantInfoViewModel.From(info, isEditable: false),
    };

    /// <summary>Runs the list action as the given user.</summary>
    private ApplicationListViewModel Index(string userId, string role, ApplicationListViewModel parameters) =>
        Assert.IsType<ApplicationListViewModel>(Assert.IsType<ViewResult>(Controller(userId, role).Index(parameters)).Model);

    /// <summary>Asserts a Post/Redirect/Get to the given section of the application page.</summary>
    private static void AssertRedirectToSection(IActionResult result, ApplicationSection section)
    {
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ApplicationsController.Edit), redirect.ActionName);
        Assert.Equal(section, redirect.RouteValues!["section"]);
    }

    /// <summary>Starts a draft through the service.</summary>
    private async Task<int> StartAsync(string applicantId, int unitId)
    {
        await using var db = NewContext();
        return (await new ApplicationService(db, _clock).StartAsync(unitId, applicantId)).Value;
    }

    /// <summary>A draft with both sections saved (ready to submit), built through the service.</summary>
    private async Task<int> CompleteAsync(string applicantId, int unitId)
    {
        await using var db = NewContext();
        var service = new ApplicationService(db, _clock);
        var id = (await service.StartAsync(unitId, applicantId)).Value;
        await service.SaveApplicantInfoAsync(id, applicantId, ApplicationRulesTests.ValidInfo);
        await service.AddResidenceAsync(id, applicantId, ApplicationRulesTests.ValidResidence);
        await service.CompleteResidenceHistoryAsync(id, applicantId);
        return id;
    }

    /// <summary>A submitted application claimed by <paramref name="managerId"/> through the review service.</summary>
    private async Task<int> ClaimedAsync(string applicantId, int unitId, string managerId)
    {
        var id = await SubmittedAsync(applicantId, unitId);
        await using var db = NewContext();
        Assert.True((await new ApplicationReviewService(db, _clock).ClaimAsync(id, managerId)).Succeeded);
        return id;
    }

    /// <summary>A complete application, submitted.</summary>
    private async Task<int> SubmittedAsync(string applicantId, int unitId)
    {
        var id = await CompleteAsync(applicantId, unitId);
        await using var db = NewContext();
        await new ApplicationService(db, _clock).SubmitAsync(id, applicantId);
        return id;
    }

    /// <summary>Reads an application fresh from the database.</summary>
    private async Task<RentalApplication> LoadAsync(int id)
    {
        await using var db = NewContext();
        return await db.RentalApplications.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    /// <summary>Everything a save would change: status, Section 1 values, flags, timestamp and concurrency stamp.</summary>
    private async Task<string> SnapshotAsync(int id)
    {
        var a = await LoadAsync(id);
        return $"{a.Status}|{a.FirstName}|{a.Phone}|{a.ApplicantInfoCompleted}|{a.ResidenceHistoryCompleted}|{a.UpdatedAt:O}|{a.ConcurrencyStamp}";
    }

    /// <summary>A property with one unit.</summary>
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

    /// <summary>TempData storage that lives only for the test.</summary>
    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _values = new Dictionary<string, object>();

        /// <summary>Returns the stored values.</summary>
        public IDictionary<string, object> LoadTempData(HttpContext context) => _values;

        /// <summary>Stores the values.</summary>
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) => _values = values;
    }

    /// <summary>Builds "/{controller}/{action}/{id}" links without the routing system; only "/" paths are local.</summary>
    private sealed class StubUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();

        public string? Action(UrlActionContext actionContext) =>
            $"/{actionContext.Controller ?? "Applications"}/{actionContext.Action}/{new RouteValueDictionary(actionContext.Values)["id"]}";

        public string? Content(string? contentPath) => contentPath;

        public bool IsLocalUrl(string? url) => url is not null && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal);

        public string? Link(string? routeName, object? values) => null;

        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }
}
