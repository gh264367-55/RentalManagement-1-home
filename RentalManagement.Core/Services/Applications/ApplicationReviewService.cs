using Microsoft.EntityFrameworkCore;
using RentalManagement.Core.Data;
using RentalManagement.Core.Domain;

namespace RentalManagement.Core.Services.Applications;

/// <summary>
/// Manages application claims, releases, reviews, and status history.
/// Ensures only the claiming manager can release or review an application.
/// Uses concurrency stamps to prevent conflicting claims and lease approvals.
/// Validates lease date overlaps to prevent multiple leases for the same unit.
/// Leaves other pending applications unchanged until individually reviewed.
/// </summary>
public sealed class ApplicationReviewService(ApplicationDbContext db, TimeProvider clock)
{
    private const string NotFoundMessage = "Application not found.";

    private const string ConcurrentMessage =
        "Another review of this application, or an approval for the same unit, was saved at the same moment. " +
        "Nothing was changed. Reload the page to see the current state.";

    private const string ConcurrentClaimMessage =
        "Another property manager claimed or changed this application at the same moment, so your claim was not saved. " +
        "The queue has been refreshed.";

    /// <summary>
    /// Claims a Submitted application for review: Submitted → Under Review, with the manager as claim owner, the claim
    /// time, and a history row. Refused for an application already claimed (by anyone), for any other status, and when
    /// another manager's claim or change commits first.
    /// </summary>
    /// <param name="applicationId">The application to claim.</param>
    /// <param name="managerId">The signed-in user; must be a property manager.</param>
    /// <param name="cancellationToken">Cancels the database work.</param>
    /// <returns>
    /// Success; NotFound for an unknown application or a caller who is not a property manager; Conflict when it is
    /// already claimed, isn't Submitted, or a concurrent change won.
    /// </returns>
    public async Task<OperationResult> ClaimAsync(int applicationId, string managerId, CancellationToken cancellationToken = default)
    {
        if (!await IsPropertyManagerAsync(managerId, cancellationToken))
        {
            return OperationResult.NotFound(NotFoundMessage);
        }

        var application = await db.RentalApplications.SingleOrDefaultAsync(a => a.Id == applicationId, cancellationToken);
        if (application is null)
        {
            return OperationResult.NotFound(NotFoundMessage);
        }

        if (application.Status == ApplicationStatus.UnderReview)
        {
            return application.ClaimedByUserId == managerId
                ? OperationResult.Conflict("You have already claimed this application. Find it under My claimed applications.")
                : OperationResult.Conflict(
                    $"{await ClaimOwnerNameAsync(application, cancellationToken)} has already claimed this application.");
        }

        if (application.Status != ApplicationStatus.Submitted)
        {
            return OperationResult.Conflict(
                $"This application is {application.Status.InSentence()}; only submitted applications can be claimed.");
        }

        application.Claim(managerId, clock.GetUtcNow());
        return await SaveAsync(ConcurrentClaimMessage, cancellationToken);
    }

    /// <summary>
    /// Releases the manager's own claim: Under Review → Submitted, clearing the claim owner and time and writing a
    /// history row. The application keeps its original submission date, so it returns to its place in the queue.
    /// </summary>
    /// <param name="applicationId">The application to release.</param>
    /// <param name="managerId">The signed-in user; must be the property manager who claimed it.</param>
    /// <param name="cancellationToken">Cancels the database work.</param>
    /// <returns>
    /// Success; NotFound for an unknown application or a caller who is not a property manager; Conflict when it isn't
    /// Under Review, is claimed by another manager, or a concurrent change won.
    /// </returns>
    public async Task<OperationResult> ReleaseAsync(int applicationId, string managerId, CancellationToken cancellationToken = default)
    {
        if (!await IsPropertyManagerAsync(managerId, cancellationToken))
        {
            return OperationResult.NotFound(NotFoundMessage);
        }

        var application = await db.RentalApplications.SingleOrDefaultAsync(a => a.Id == applicationId, cancellationToken);
        if (application is null)
        {
            return OperationResult.NotFound(NotFoundMessage);
        }

        var refusal = await CheckClaimOwnerAsync(application, managerId, "release", cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        application.Release(managerId, clock.GetUtcNow());
        return await SaveAsync(ConcurrentMessage, cancellationToken);
    }

    /// <summary>
    /// Records a review outcome on an application the reviewer has claimed. Approve moves it to Approved and issues
    /// the lease; Return sends it back to the applicant for correction; Deny closes it. Every outcome clears the claim
    /// and writes a history row (reviewer, time, outcome, comment) through <see cref="RentalApplication.TransitionTo"/>.
    /// </summary>
    /// <param name="applicationId">The application to review.</param>
    /// <param name="reviewerId">The signed-in user; must be the property manager who claimed it.</param>
    /// <param name="input">Outcome, comment and (for Approve) the lease start date.</param>
    /// <param name="cancellationToken">Cancels the database work.</param>
    /// <returns>
    /// Success; Validation errors on the fields; NotFound for an unknown application or a reviewer who is not a
    /// property manager; Conflict when the application is not Under Review, is claimed by another manager, the unit
    /// already has an overlapping lease, or a concurrent change won.
    /// </returns>
    public async Task<OperationResult> ReviewAsync(
        int applicationId, string reviewerId, ReviewInput input, CancellationToken cancellationToken = default)
    {
        // The controller already requires the role; the service re-checks so no other caller can skip it.
        if (!await IsPropertyManagerAsync(reviewerId, cancellationToken))
        {
            return OperationResult.NotFound(NotFoundMessage);
        }

        var application = await db.RentalApplications.SingleOrDefaultAsync(a => a.Id == applicationId, cancellationToken);
        if (application is null)
        {
            return OperationResult.NotFound(NotFoundMessage);
        }

        var refusal = await CheckClaimOwnerAsync(application, reviewerId, "review", cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var today = clock.GetToday();
        input = input with { Comment = string.IsNullOrWhiteSpace(input.Comment) ? null : input.Comment.Trim() };
        var errors = ApplicationRules.ValidateReview(input, today);
        if (errors.Count > 0)
        {
            return OperationResult.Invalid(errors);
        }

        var outcome = input.Outcome!.Value;
        var now = clock.GetUtcNow();

        if (outcome == ReviewOutcome.Approve)
        {
            // Load (and track) the unit first: its concurrency stamp is the guard against a racing approval.
            var unit = await db.Units.SingleAsync(u => u.Id == application.UnitId, cancellationToken);
            var start = input.LeaseStartDate!.Value;
            var end = Lease.EndDateFor(start);

            var overlapping = await db.Leases
                .Where(l => l.UnitId == unit.Id && l.StartDate <= end && l.EndDate >= start)
                .OrderBy(l => l.StartDate)
                .Select(l => new { l.StartDate, l.EndDate })
                .FirstOrDefaultAsync(cancellationToken);
            if (overlapping is not null)
            {
                var activeNow = overlapping.StartDate <= today && overlapping.EndDate >= today;
                return OperationResult.Conflict(
                    $"Unit {unit.UnitNumber} already has {(activeNow ? "an active lease" : "a lease")} " +
                    $"({overlapping.StartDate:MMM d, yyyy} – {overlapping.EndDate:MMM d, yyyy}) that overlaps the new term " +
                    $"({start:MMM d, yyyy} – {end:MMM d, yyyy}). The application stays under review; return or deny it instead.");
            }

            application.TransitionTo(ApplicationStatus.Approved, reviewerId, now, outcome, input.Comment);
            application.Lease = Lease.Issue(application, unit.MonthlyRent, start, now);
            unit.ConcurrencyStamp = Guid.NewGuid();
        }
        else
        {
            application.TransitionTo(outcome.ToStatus(), reviewerId, now, outcome, input.Comment);
        }

        return await SaveAsync(ConcurrentMessage, cancellationToken);
    }

    /// <summary>
    /// Whether <paramref name="userId"/> may release or review the application now: it must be Under Review and claimed
    /// by that user. Returns null when allowed, or the Conflict explaining why not.
    /// </summary>
    /// <param name="application">The tracked application.</param>
    /// <param name="userId">The signed-in manager.</param>
    /// <param name="action">"release" or "review", for the message.</param>
    /// <param name="cancellationToken">Cancels the name lookup.</param>
    /// <returns>Null, or the refusal.</returns>
    private async Task<OperationResult?> CheckClaimOwnerAsync(
        RentalApplication application, string userId, string action, CancellationToken cancellationToken)
    {
        if (application.IsClaimedBy(userId))
        {
            return null;
        }

        if (application.Status == ApplicationStatus.Submitted)
        {
            return OperationResult.Conflict(action == "review"
                ? "Claim this application from the review queue before reviewing it."
                : "This application isn't claimed, so there is nothing to release.");
        }

        if (application.Status == ApplicationStatus.UnderReview)
        {
            return OperationResult.Conflict(
                $"{await ClaimOwnerNameAsync(application, cancellationToken)} has claimed this application. " +
                $"Only the manager who claimed it can {action} it.");
        }

        return OperationResult.Conflict(
            $"This application is {application.Status.InSentence()}; only applications under review can be {(action == "review" ? "reviewed" : "released")}.");
    }

    /// <summary>Saves, turning a lost race into a Conflict with nothing of this attempt saved.</summary>
    private async Task<OperationResult> SaveAsync(string concurrentMessage, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            // The racing change won. Forget this attempt's pending changes so the context can't save them later.
            db.ChangeTracker.Clear();
            return OperationResult.Conflict(concurrentMessage);
        }
    }

    /// <summary>The claim owner's full name, for messages ("Another property manager" if it can't be read).</summary>
    private async Task<string> ClaimOwnerNameAsync(RentalApplication application, CancellationToken cancellationToken)
    {
        var name = await db.Users
            .Where(u => u.Id == application.ClaimedByUserId)
            .Select(u => u.FirstName + " " + u.LastName)
            .FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(name) ? "Another property manager" : name.Trim();
    }

    /// <summary>
    /// The application's status history, oldest first, with the name and email of the user behind each change.
    /// Callers must already have checked that the user may see it (property managers only).
    /// </summary>
    /// <param name="applicationId">The application id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The history entries; empty for an unknown application.</returns>
    public async Task<IReadOnlyList<StatusHistoryEntry>> GetStatusHistoryAsync(int applicationId, CancellationToken cancellationToken = default) =>
        await db.ApplicationStatusHistory
            .AsNoTracking()
            .Where(h => h.RentalApplicationId == applicationId)
            .OrderBy(h => h.ChangedAt).ThenBy(h => h.Id)
            .Select(h => new StatusHistoryEntry(
                h.ChangedAt,
                h.ChangedBy.FirstName + " " + h.ChangedBy.LastName,
                h.ChangedBy.Email,
                h.FromStatus,
                h.ToStatus,
                h.Outcome,
                h.Comment))
            .ToListAsync(cancellationToken);

    /// <summary>Whether the user holds the PropertyManager role, read from Identity's tables.</summary>
    internal static Task<bool> IsPropertyManagerAsync(ApplicationDbContext db, string userId, CancellationToken cancellationToken) =>
        db.UserRoles.AnyAsync(
            ur => ur.UserId == userId && db.Roles.Any(r => r.Id == ur.RoleId && r.Name == Roles.PropertyManager),
            cancellationToken);

    /// <summary>Whether the user holds the PropertyManager role.</summary>
    private Task<bool> IsPropertyManagerAsync(string userId, CancellationToken cancellationToken) =>
        IsPropertyManagerAsync(db, userId, cancellationToken);
}
