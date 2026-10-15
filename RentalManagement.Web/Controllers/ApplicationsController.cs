using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services;
using RentalManagement.Core.Services.Applications;
using RentalManagement.Web.Infrastructure;
using RentalManagement.Web.ViewComponents;
using RentalManagement.Web.ViewModels;

namespace RentalManagement.Web.Controllers;

/// <summary>
/// The rental application. <see cref="Edit(int, ApplicationSection, CancellationToken)"/> shows one section at a time
/// on a single page; <see cref="Edit(int, ApplicationPageViewModel, string?, CancellationToken)"/> is the one action
/// the page's one form posts to, and the clicked button decides what happens. Applicants see and change only their
/// own applications; property managers can open any application read-only, review submitted ones through the review
/// modal, and see each application's status history.
/// </summary>
[Authorize(Roles = $"{Roles.Applicant},{Roles.PropertyManager}")]
public sealed class ApplicationsController(
    ApplicationService applications, ApplicationReviewService reviews, TimeProvider clock) : AppController
{
    /// <summary>Regions refreshed after a review: the status card and the history.</summary>
    public const string ReviewRefreshRegions = "#status-panel #status-history";

    /// <summary>Value of the "Save &amp; continue" button: validate and save the section, then go to the next one.</summary>
    public const string CommandContinue = "continue";

    /// <summary>Value of the "Back" button: go to the previous section without saving.</summary>
    public const string CommandBack = "back";

    /// <summary>Value of the "Submit/Resubmit application" button on the Summary.</summary>
    public const string CommandSubmit = "submit";

    /// <summary>Whether the signed-in user is a property manager (read-only access to every application).</summary>
    private bool IsManager => User.IsInRole(Roles.PropertyManager);
        
    [HttpGet]
    public IActionResult Index(ApplicationListViewModel model)
    {
        // Invalid parameters (e.g. ?status=Bogus or ?pageSize=7) don't fail the page: the grid starts from the defaults
        // and says so.
        var parsed = ModelState.IsValid ? model.ToQuery() : null;
        var valid = parsed is { Succeeded: true };
        model.IsManager = IsManager;
        model.Grid = new ApplicationGridOptions
        {
            Id = "application-grid",
            Caption = IsManager ? "Applications" : "My applications",
            Query = valid ? parsed!.Value! : ApplicationListQuery.Default,
            SyncUrl = true,
            Notice = valid ? null : "Some list settings in the address were not valid, so the default list is shown.",
        };
        return View(model);
    }

    /// <summary>Starts a draft for a unit (or resumes the applicant's open one) and opens it.</summary>
    /// <param name="unitId">The unit applied for.</param>
    /// <param name="cancellationToken">Cancels the database work.</param>
    /// <returns>A redirect to the application, or back to Available units with an error message.</returns>
    [HttpPost, Authorize(Roles = Roles.Applicant)]
    public async Task<IActionResult> Start(int unitId, CancellationToken cancellationToken)
    {
        var result = await applications.StartAsync(unitId, CurrentUserId, cancellationToken);
        if (!result.Succeeded)
        {
            TempData[TempDataKeys.Error] = result.Error;
            return RedirectToAction(nameof(UnitsController.Available), "Units");
        }

        return RedirectToAction(nameof(Edit), new { id = result.Value });
    }

    /// <summary>
    /// Shows one section of the application page. Owners of a Draft/Returned application get the editable form;
    /// everyone else allowed to see it (the owner afterwards, any property manager) gets it read-only.
    /// </summary>
    /// <param name="id">The application id.</param>
    /// <param name="section">The section to show; defaults to Applicant information.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>The page, or 404 when the application isn't visible to the user.</returns>
    [HttpGet]
    public async Task<IActionResult> Edit(int id, ApplicationSection section = ApplicationSection.ApplicantInfo, CancellationToken cancellationToken = default)
    {
        var page = await BuildPageAsync(id, section, cancellationToken);
        return page is null ? NotFound() : View(page);
    }

    /// <summary>
    /// Handles application navigation, section validation, and submission.
    /// Saves valid sections when continuing and navigates back without saving.
    /// Submits or resubmits applications from the Summary section.
    /// Rejects unauthorized or non-editable application updates.
    /// </summary>
    [HttpPost, Authorize(Roles = Roles.Applicant)]
    public async Task<IActionResult> Edit([FromRoute] int id, ApplicationPageViewModel input, string? command, CancellationToken cancellationToken)
    {
        var page = await BuildPageAsync(id, input.CurrentSection, cancellationToken);
        if (page is null || !page.Header.IsOwner)
        {
            return NotFound();
        }

        if (command == CommandBack)
        {
            // Back never saves, so it needs no validation and works whatever the status.
            return RedirectToSection(id, page.PreviousSection ?? ApplicationSection.ApplicantInfo);
        }

        if (!page.IsEditable)
        {
            ModelState.Clear();
            ModelState.AddModelError(string.Empty,
                $"This application is {page.Header.Details.Status.InSentence()} and can no longer be changed.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            return View(page);
        }

        return (command, input.CurrentSection) switch
        {
            (CommandContinue, ApplicationSection.ApplicantInfo) => await ContinueApplicantInfoAsync(page, input.ApplicantInfo, cancellationToken),
            (CommandContinue, ApplicationSection.ResidenceHistory) => await ContinueResidenceHistoryAsync(page, cancellationToken),
            (CommandSubmit, ApplicationSection.Summary) => await SubmitAsync(page, cancellationToken),
            _ => BadRequest(),
        };
    }

    /// <summary>The withdraw confirmation modal (or, with 422, why the application can no longer be withdrawn).</summary>
    /// <param name="id">The application id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The confirmation partial, or 404 for someone else's application.</returns>
    [HttpGet, Authorize(Roles = Roles.Applicant)]
    public async Task<IActionResult> Withdraw(int id, CancellationToken cancellationToken)
    {
        var details = await applications.GetAsync(id, CurrentUserId, isManager: false, cancellationToken);
        if (details is null)
        {
            return NotFound();
        }

        var model = WithdrawModel(details);
        if (!details.IsWithdrawableBy(CurrentUserId))
        {
            ModelState.AddModelError(string.Empty, "This application can no longer be withdrawn.");
            return ModalInvalid("_Confirm", model);
        }

        return PartialView("_Confirm", model);
    }

    /// <summary>
    /// Withdraws the application. On success the modal closes and the page reloads (the application is now
    /// read-only); a refusal re-renders the confirmation with the reason.
    /// </summary>
    /// <param name="id">The application id.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>JSON success, the confirmation partial with 422, or 404.</returns>
    [HttpPost, ActionName(nameof(Withdraw)), Authorize(Roles = Roles.Applicant)]
    public async Task<IActionResult> WithdrawConfirmed([FromRoute] int id, CancellationToken cancellationToken)
    {
        var result = await applications.WithdrawAsync(id, CurrentUserId, cancellationToken);
        if (result.Kind == FailureKind.NotFound && !result.Succeeded)
        {
            return NotFound();
        }

        if (!result.Succeeded)
        {
            AddError(result);
            var details = await applications.GetAsync(id, CurrentUserId, isManager: false, cancellationToken);
            return ModalInvalid("_Confirm", WithdrawModel(details!));
        }

        TempData[TempDataKeys.Success] = "Your application was withdrawn.";
        return ModalSuccess();
    }

    /// <summary>The status card view component alone, for refreshing <c>#status-panel</c>.</summary>
    /// <param name="id">The application id; the view component re-checks access.</param>
    /// <returns>The rendered view component.</returns>
    [HttpGet]
    public IActionResult StatusPanel(int id) => ViewComponent(typeof(ApplicationStatusViewComponent), new { applicationId = id });

    // ---- Review queue, claim and release (property managers) --------------------------------------------------

    /// <summary>
    /// The review queue: "Available for review" (Submitted, unclaimed, with Claim) and "My claimed applications"
    /// (Under Review, claimed by the signed-in manager, with Review and Release). Both are ApplicationGrid view
    /// components whose rows come from <c>GET /api/applications/review-queue</c>, filtered, sorted and paged in SQL.
    /// </summary>
    /// <returns>The queue page.</returns>
    [HttpGet, Authorize(Roles = Roles.PropertyManager)]
    public IActionResult ReviewQueue() => View(new ReviewQueueViewModel
    {
        Available = new ApplicationGridOptions
        {
            Id = "queue-available",
            Caption = "Available for review",
            Queue = Core.Services.Applications.ReviewQueue.Available,
            Query = ReviewQueueService.DefaultQuery(Core.Services.Applications.ReviewQueue.Available),
            ShowStatusFilter = false,
            EmptyText = "No submitted applications are waiting to be claimed.",
        },
        MyClaims = new ApplicationGridOptions
        {
            Id = "queue-mine",
            Caption = "My claimed applications",
            Queue = Core.Services.Applications.ReviewQueue.MyClaims,
            Query = ReviewQueueService.DefaultQuery(Core.Services.Applications.ReviewQueue.MyClaims),
            ShowStatusFilter = false,
            EmptyText = "You haven't claimed any applications. Claim one from the list above to review it.",
        },
    });

    /// <summary>
    /// Claims a Submitted application for the signed-in manager (Submitted → Under Review). The claim owner is always
    /// the authenticated user; nothing about the claim is read from the request. From site.js (fetch) the answer is
    /// JSON: success, or <c>{ success: false, message }</c> with 404/409 (e.g. another manager claimed it first).
    /// Without script it redirects back with a one-time message.
    /// </summary>
    /// <param name="id">The application id (from the route).</param>
    /// <param name="returnUrl">Where a no-script post returns to (local URLs only); default the application page.</param>
    /// <param name="cancellationToken">Cancels the database work.</param>
    /// <returns>JSON for fetch requests; otherwise a redirect, or 404.</returns>
    [HttpPost, Authorize(Roles = Roles.PropertyManager)]
    public async Task<IActionResult> Claim([FromRoute] int id, string? returnUrl, CancellationToken cancellationToken)
    {
        var result = await reviews.ClaimAsync(id, CurrentUserId, cancellationToken);
        var message = $"Application #{id} claimed. It is now under review by you.";

        if (Request.IsAjaxRequest())
        {
            return result.Succeeded ? ModalSuccess(message) : ActionFailed(result);
        }

        if (!result.Succeeded && result.Kind == FailureKind.NotFound)
        {
            return NotFound();
        }

        TempData[result.Succeeded ? TempDataKeys.Success : TempDataKeys.Error] = result.Succeeded ? message : result.Error;
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Edit), new { id });
    }

    /// <summary>
    /// The release confirmation modal. Only the manager who holds the claim gets the confirm button; anyone else gets
    /// the reason (422), so a stale queue row can't be used to release someone else's claim.
    /// </summary>
    /// <param name="id">The application id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The confirmation partial, the partial with the reason (422), or 404.</returns>
    [HttpGet, Authorize(Roles = Roles.PropertyManager)]
    public async Task<IActionResult> Release(int id, CancellationToken cancellationToken)
    {
        var details = await applications.GetAsync(id, CurrentUserId, isManager: true, cancellationToken);
        if (details is null)
        {
            return NotFound();
        }

        var model = ReleaseModel(details);
        if (!details.IsClaimedBy(CurrentUserId))
        {
            ModelState.AddModelError(string.Empty, NotYourClaimMessage(details, "release"));
            return ModalInvalid("_Confirm", model);
        }

        return PartialView("_Confirm", model);
    }

    /// <summary>
    /// Releases the signed-in manager's claim (Under Review → Submitted), so another manager can claim it. On success
    /// the modal closes and the status card, history and any queue grids refresh; a refusal re-renders the
    /// confirmation with the reason.
    /// </summary>
    /// <param name="id">The application id (from the route).</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>JSON success, the confirmation partial with 422, or 404.</returns>
    [HttpPost, ActionName(nameof(Release)), Authorize(Roles = Roles.PropertyManager)]
    public async Task<IActionResult> ReleaseConfirmed([FromRoute] int id, CancellationToken cancellationToken)
    {
        var result = await reviews.ReleaseAsync(id, CurrentUserId, cancellationToken);
        if (!result.Succeeded && result.Kind == FailureKind.NotFound)
        {
            return NotFound();
        }

        if (!result.Succeeded)
        {
            AddError(result);
            var details = await applications.GetAsync(id, CurrentUserId, isManager: true, cancellationToken);
            return details is null ? NotFound() : ModalInvalid("_Confirm", ReleaseModel(details));
        }

        return ModalSuccess($"Application #{id} released. It is back in the queue for any manager to claim.");
    }

    // ---- Review (property managers) ---------------------------------------------------------------------------

    /// <summary>
    /// The review modal for an application the signed-in manager has claimed, with the lease start date defaulting to
    /// today. Otherwise it explains (409) why it can't be reviewed: not claimed yet, claimed by another manager, or no
    /// longer under review.
    /// </summary>
    /// <param name="id">The application id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The review form partial, a 409 explanation, or 404.</returns>
    [HttpGet, Authorize(Roles = Roles.PropertyManager)]
    public async Task<IActionResult> Review(int id, CancellationToken cancellationToken)
    {
        var details = await applications.GetAsync(id, CurrentUserId, isManager: true, cancellationToken);
        if (details is null)
        {
            return NotFound();
        }

        if (!details.IsClaimedBy(CurrentUserId))
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            return PartialView("_ModalMessage", NotYourClaimMessage(details, "review"));
        }

        var model = new ReviewFormViewModel { ApplicationId = id, LeaseStartDate = clock.GetToday() };
        Describe(model, details);
        return PartialView("_ReviewForm", model);
    }

    /// <summary>
    /// Records the review. Invalid input or a refusal (not submitted any more, unit already leased, a concurrent change)
    /// re-renders the modal with the reason; success closes it and refreshes the status card and history.
    /// </summary>
    /// <param name="id">The application id (from the route).</param>
    /// <param name="model">The posted review form.</param>
    /// <param name="cancellationToken">Cancels the database work.</param>
    /// <returns>JSON success, the form with errors (422), or 404.</returns>
    [HttpPost, Authorize(Roles = Roles.PropertyManager)]
    public async Task<IActionResult> Review([FromRoute] int id, ReviewFormViewModel model, CancellationToken cancellationToken)
    {
        var details = await applications.GetAsync(id, CurrentUserId, isManager: true, cancellationToken);
        if (details is null)
        {
            return NotFound();
        }

        model.ApplicationId = id;
        Describe(model, details);
        if (!ModelState.IsValid)
        {
            return ModalInvalid("_ReviewForm", model);
        }

        var result = await reviews.ReviewAsync(id, CurrentUserId, model.ToInput(), cancellationToken);
        if (result.Kind == FailureKind.NotFound && !result.Succeeded)
        {
            return NotFound();
        }

        if (!result.Succeeded)
        {
            AddError(result);
            return ModalInvalid("_ReviewForm", model);
        }

        return ModalSuccess(model.Outcome switch
        {
            ReviewOutcome.Approve => $"Application approved. Lease issued from {model.LeaseStartDate:MMM d, yyyy}.",
            ReviewOutcome.Return => "Application returned to the applicant for corrections.",
            _ => "Application denied.",
        });
    }

    /// <summary>The status history view component alone, for refreshing <c>#status-history</c>.</summary>
    /// <param name="id">The application id.</param>
    /// <returns>The rendered view component.</returns>
    [HttpGet, Authorize(Roles = Roles.PropertyManager)]
    public IActionResult History(int id) => ViewComponent(typeof(StatusHistoryViewComponent), new { applicationId = id });

    /// <summary>Why the signed-in manager can't release or review this application (it isn't their claim).</summary>
    private static string NotYourClaimMessage(ApplicationDetails details, string action) => details.Status switch
    {
        ApplicationStatus.Submitted => action == "review"
            ? "Claim this application before reviewing it, so no other manager reviews it at the same time."
            : "This application isn't claimed, so there is nothing to release.",
        ApplicationStatus.UnderReview =>
            $"{details.ClaimedByName} has claimed this application. Only the manager who claimed it can {action} it.",
        _ => $"This application is {details.Status.InSentence()}; only applications under review can be {(action == "review" ? "reviewed" : "released")}.",
    };

    /// <summary>The release confirmation dialog; on success the status card, history and queue grids refresh.</summary>
    private ConfirmViewModel ReleaseModel(ApplicationDetails details) => new()
    {
        Title = "Release application",
        Message = $"Release application #{details.Id} ({details.ApplicantName}, unit {details.UnitNumber} at " +
                  $"{details.PropertyName}) back to the review queue? It returns to Submitted and any property manager " +
                  "can claim it.",
        ActionUrl = Url.Action(nameof(Release), new { id = details.Id })!,
        Refresh = ReviewRefreshRegions,
        ConfirmText = "Release to queue",
        ConfirmCss = "btn-warning",
    };

    /// <summary>Fills the read-only context shown in the review dialog from the database.</summary>
    private static void Describe(ReviewFormViewModel model, ApplicationDetails details)
    {
        model.UnitLabel = $"Unit {details.UnitNumber} · {details.PropertyName}";
        model.ApplicantName = details.ApplicantName;
        model.MonthlyRent = details.MonthlyRent;
    }

    // ---- Section handlers -------------------------------------------------------------------------------------

    /// <summary>
    /// "Save &amp; continue" on Section 1: when the posted fields pass the view-model checks, the service validates and
    /// saves them and the user moves to Residence history. Otherwise nothing is saved and the section re-renders with
    /// what was typed and the errors.
    /// </summary>
    private async Task<IActionResult> ContinueApplicantInfoAsync(
        ApplicationPageViewModel page, ApplicantInfoViewModel posted, CancellationToken cancellationToken)
    {
        if (ModelState.IsValid)
        {
            var result = await applications.SaveApplicantInfoAsync(page.Id, CurrentUserId, posted.ToInput(), cancellationToken);
            if (result.Succeeded)
            {
                return RedirectToSection(page.Id, ApplicationSection.ResidenceHistory);
            }

            if (result.Kind == FailureKind.NotFound)
            {
                return NotFound();
            }

            AddError(result, nameof(ApplicationPageViewModel.ApplicantInfo));
        }

        // Invalid: nothing was saved; show the same section with what the user typed and the errors.
        posted.IsEditable = true;
        page.ApplicantInfo = posted;
        return View(page);
    }

    /// <summary>
    /// "Save &amp; continue" on Section 2: marks Residence history complete when it has at least one valid residence and
    /// moves to the Summary; otherwise re-renders the section with the reason.
    /// </summary>
    private async Task<IActionResult> ContinueResidenceHistoryAsync(ApplicationPageViewModel page, CancellationToken cancellationToken)
    {
        // This section has no fields in the main form (residences are saved through their modal), so the empty
        // ApplicantInfo binding is irrelevant.
        ModelState.Clear();
        var result = await applications.CompleteResidenceHistoryAsync(page.Id, CurrentUserId, cancellationToken);
        if (result.Succeeded)
        {
            return RedirectToSection(page.Id, ApplicationSection.Summary);
        }

        AddError(result);
        return View(page);
    }

    /// <summary>
    /// "Submit" on the Summary: submits a Draft or resubmits a Returned application, then shows the Summary with a
    /// confirmation. Blockers (unsaved section, invalid data, leased unit) re-render the Summary with the reasons.
    /// </summary>
    private async Task<IActionResult> SubmitAsync(ApplicationPageViewModel page, CancellationToken cancellationToken)
    {
        ModelState.Clear();
        var resubmission = page.Header.Details.Status == ApplicationStatus.Returned;
        var result = await applications.SubmitAsync(page.Id, CurrentUserId, cancellationToken);
        if (result.Succeeded)
        {
            TempData[TempDataKeys.Success] = resubmission
                ? "Your corrected application was resubmitted for review."
                : "Your application was submitted for review.";
            return RedirectToSection(page.Id, ApplicationSection.Summary);
        }

        AddError(result);
        return View(page);
    }

    // ---- Page model -------------------------------------------------------------------------------------------

    /// <summary>Builds the page from the database; editability is decided here, on the server.</summary>
    /// <param name="id">The application id.</param>
    /// <param name="section">The section to show (an undefined value falls back to Section 1).</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>The page model, or null when the application isn't visible to the user.</returns>
    private async Task<ApplicationPageViewModel?> BuildPageAsync(int id, ApplicationSection section, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId;
        var isManager = IsManager;
        var details = await applications.GetAsync(id, userId, isManager, cancellationToken);
        if (details is null)
        {
            return null;
        }

        var isOwner = details.ApplicantId == userId;
        var isEditable = details.IsEditableBy(userId);
        if (!Enum.IsDefined(section))
        {
            section = ApplicationSection.ApplicantInfo;
        }

        return new ApplicationPageViewModel
        {
            Id = id,
            CurrentSection = section,
            Header = new ApplicationHeader(details, isOwner, isEditable, isManager),
            ApplicantInfo = ApplicantInfoViewModel.From(details.ApplicantInfo, isEditable),
            SubmitBlockers = isEditable && section == ApplicationSection.Summary
                ? await applications.GetSubmitBlockersAsync(id, userId, cancellationToken)
                : [],
        };
    }

    /// <summary>Post/Redirect/Get to a section of the application page.</summary>
    private RedirectToActionResult RedirectToSection(int id, ApplicationSection section) =>
        RedirectToAction(nameof(Edit), new { id, section });

    /// <summary>The withdraw confirmation dialog; on success the whole page reloads.</summary>
    private ConfirmViewModel WithdrawModel(ApplicationDetails details) => new()
    {
        Title = "Withdraw application",
        Message = $"Withdraw your application for unit {details.UnitNumber} at {details.PropertyName}? " +
                  "A withdrawn application can't be reopened; you would need to start a new one.",
        ActionUrl = Url.Action(nameof(Withdraw), new { id = details.Id })!,
        Refresh = "page",
        ConfirmText = "Withdraw application",
    };
}
