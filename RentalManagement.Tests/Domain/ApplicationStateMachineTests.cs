// =====================================================================================================================
// Module: RentalManagement.Tests / Domain / ApplicationStateMachineTests.cs
// Purpose: Unit tests of the application lifecycle: which status transitions are legal (including claim → Under Review
//          and release), which statuses are terminal or editable, how RentalApplication.TransitionTo / Claim / Release
//          record history and the claim, and how review outcomes map to statuses.
// =====================================================================================================================

using RentalManagement.Core.Domain;

namespace RentalManagement.Tests.Domain;

/// <summary>Tests of <see cref="ApplicationStateMachine"/> and <see cref="RentalApplication"/>'s status methods.</summary>
public class ApplicationStateMachineTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Every move the workflow allows is accepted by the transition table.</summary>
    [Theory]
    [InlineData(ApplicationStatus.Draft, ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.Draft, ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Submitted, ApplicationStatus.UnderReview)]
    [InlineData(ApplicationStatus.Submitted, ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.UnderReview, ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.UnderReview, ApplicationStatus.Returned)]
    [InlineData(ApplicationStatus.UnderReview, ApplicationStatus.Denied)]
    [InlineData(ApplicationStatus.UnderReview, ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.UnderReview, ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Returned, ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.Returned, ApplicationStatus.Withdrawn)]
    public void Allows_legal_transitions(ApplicationStatus from, ApplicationStatus to) =>
        Assert.True(ApplicationStateMachine.CanTransition(from, to));

    /// <summary>
    /// Shortcuts are refused: approving a draft, moving backwards, and (since the review queue) recording an outcome on
    /// a Submitted application that nobody has claimed.
    /// </summary>
    [Theory]
    [InlineData(ApplicationStatus.Draft, ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Draft, ApplicationStatus.Returned)]
    [InlineData(ApplicationStatus.Draft, ApplicationStatus.UnderReview)]
    [InlineData(ApplicationStatus.Returned, ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Returned, ApplicationStatus.UnderReview)]
    [InlineData(ApplicationStatus.Submitted, ApplicationStatus.Draft)]
    [InlineData(ApplicationStatus.Submitted, ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Submitted, ApplicationStatus.Returned)]
    [InlineData(ApplicationStatus.Submitted, ApplicationStatus.Denied)]
    [InlineData(ApplicationStatus.UnderReview, ApplicationStatus.Draft)]
    public void Rejects_illegal_transitions(ApplicationStatus from, ApplicationStatus to) =>
        Assert.False(ApplicationStateMachine.CanTransition(from, to));

    /// <summary>Approved, Denied and Withdrawn are terminal: no status at all (Under Review included) is reachable.</summary>
    [Theory]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Denied)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public void Terminal_statuses_have_no_way_out(ApplicationStatus status)
    {
        Assert.True(ApplicationStateMachine.IsTerminal(status));
        Assert.All(Enum.GetValues<ApplicationStatus>(), to => Assert.False(ApplicationStateMachine.CanTransition(status, to)));
    }

    /// <summary>Under Review is an open status, and the only one in which an outcome can be recorded.</summary>
    [Fact]
    public void Under_review_is_open_and_the_only_reviewable_status()
    {
        Assert.False(ApplicationStateMachine.IsTerminal(ApplicationStatus.UnderReview));
        Assert.Equal([ApplicationStatus.UnderReview],
            Enum.GetValues<ApplicationStatus>().Where(ApplicationStateMachine.IsReviewable));
    }

    /// <summary>Sections are editable by the applicant in Draft and Returned only (not while under review).</summary>
    [Theory]
    [InlineData(ApplicationStatus.Draft, true)]
    [InlineData(ApplicationStatus.Returned, true)]
    [InlineData(ApplicationStatus.Submitted, false)]
    [InlineData(ApplicationStatus.UnderReview, false)]
    [InlineData(ApplicationStatus.Approved, false)]
    [InlineData(ApplicationStatus.Denied, false)]
    [InlineData(ApplicationStatus.Withdrawn, false)]
    public void Applicant_can_edit_only_Draft_and_Returned(ApplicationStatus status, bool editable) =>
        Assert.Equal(editable, ApplicationStateMachine.IsEditableByApplicant(status));

    /// <summary>
    /// Each transition appends a history row (who, from → to, outcome, trimmed comment), sets SubmittedAt on submit,
    /// and rotates the concurrency stamp.
    /// </summary>
    [Fact]
    public void TransitionTo_records_history_and_rotates_the_concurrency_stamp()
    {
        var application = RentalApplication.StartDraft(unitId: 1, applicantId: "applicant", Start);
        var stamp = application.ConcurrencyStamp;

        application.TransitionTo(ApplicationStatus.Submitted, "applicant", Start.AddHours(1));
        application.Claim("manager", Start.AddHours(2));
        application.TransitionTo(ApplicationStatus.Returned, "manager", Start.AddHours(3), ReviewOutcome.Return, "  Fix it  ");

        Assert.Equal(ApplicationStatus.Returned, application.Status);
        Assert.NotEqual(stamp, application.ConcurrencyStamp);
        Assert.Equal(Start.AddHours(1), application.SubmittedAt);
        Assert.Collection(
            application.StatusHistory,
            h => Assert.Equal((null, ApplicationStatus.Draft), (h.FromStatus, h.ToStatus)),
            h => Assert.Equal((ApplicationStatus.Draft, ApplicationStatus.Submitted), (h.FromStatus, h.ToStatus)),
            h => Assert.Equal((ApplicationStatus.Submitted, ApplicationStatus.UnderReview, "manager"), (h.FromStatus, h.ToStatus, h.ChangedById)),
            h =>
            {
                Assert.Equal((ApplicationStatus.UnderReview, ApplicationStatus.Returned), (h.FromStatus, h.ToStatus));
                Assert.Equal(ReviewOutcome.Return, h.Outcome);
                Assert.Equal("Fix it", h.Comment);
                Assert.Equal("manager", h.ChangedById);
            });
    }

    /// <summary>An illegal move is a programming error: it throws and leaves the status unchanged.</summary>
    [Fact]
    public void TransitionTo_throws_on_an_illegal_move()
    {
        var application = RentalApplication.StartDraft(1, "applicant", DateTimeOffset.UnixEpoch);

        Assert.Throws<InvalidOperationException>(() =>
            application.TransitionTo(ApplicationStatus.Approved, "manager", DateTimeOffset.UnixEpoch));
        Assert.Equal(ApplicationStatus.Draft, application.Status);
    }

    // ---- Claim and release --------------------------------------------------------------------------------------

    /// <summary>Claiming moves Submitted → Under Review and records the owner, the time and a history row.</summary>
    [Fact]
    public void Claim_records_owner_time_and_history()
    {
        var application = Submitted();
        var stamp = application.ConcurrencyStamp;

        var entry = application.Claim("manager", Start.AddDays(1));

        Assert.Equal(ApplicationStatus.UnderReview, application.Status);
        Assert.Equal(("manager", (DateTimeOffset?)Start.AddDays(1)), (application.ClaimedByUserId, application.ClaimedAt));
        Assert.True(application.IsClaimedBy("manager"));
        Assert.False(application.IsClaimedBy("other"));
        Assert.Equal((ApplicationStatus.Submitted, ApplicationStatus.UnderReview, "manager", (ReviewOutcome?)null),
            (entry.FromStatus, entry.ToStatus, entry.ChangedById, entry.Outcome));
        Assert.NotEqual(stamp, application.ConcurrencyStamp);
    }

    /// <summary>A claimed application can't be claimed again, by its owner or anyone else.</summary>
    [Fact]
    public void Claim_throws_when_already_claimed()
    {
        var application = Submitted();
        application.Claim("manager", Start);

        Assert.Throws<InvalidOperationException>(() => application.Claim("other", Start));
        Assert.Throws<InvalidOperationException>(() => application.Claim("manager", Start));
        Assert.Equal("manager", application.ClaimedByUserId);
    }

    /// <summary>Only a Submitted application can be claimed: drafts, returned and terminal ones can't.</summary>
    [Theory]
    [InlineData(ApplicationStatus.Draft)]
    [InlineData(ApplicationStatus.Returned)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Denied)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public void Claim_throws_unless_submitted(ApplicationStatus status)
    {
        var application = InStatus(status);

        Assert.Throws<InvalidOperationException>(() => application.Claim("manager2", Start));
        Assert.Equal(status, application.Status);
        Assert.Null(application.ClaimedByUserId);
    }

    /// <summary>
    /// Release by the owner returns the application to Submitted, clears the claim and keeps the original submission
    /// date (so it keeps its place in the queue).
    /// </summary>
    [Fact]
    public void Release_returns_to_submitted_clears_the_claim_and_keeps_the_submission_date()
    {
        var application = Submitted();
        var submittedAt = application.SubmittedAt;
        application.Claim("manager", Start.AddDays(1));

        var entry = application.Release("manager", Start.AddDays(2));

        Assert.Equal(ApplicationStatus.Submitted, application.Status);
        Assert.Null(application.ClaimedByUserId);
        Assert.Null(application.ClaimedAt);
        Assert.Equal(submittedAt, application.SubmittedAt);
        Assert.Equal((ApplicationStatus.UnderReview, ApplicationStatus.Submitted, "manager"), (entry.FromStatus, entry.ToStatus, entry.ChangedById));
    }

    /// <summary>Another manager can't release someone else's claim; nor can anyone release an unclaimed application.</summary>
    [Fact]
    public void Release_throws_for_anyone_but_the_claim_owner()
    {
        var unclaimed = Submitted();
        var claimed = Submitted();
        claimed.Claim("manager", Start);

        Assert.Throws<InvalidOperationException>(() => claimed.Release("other", Start));
        Assert.Throws<InvalidOperationException>(() => unclaimed.Release("manager", Start));
        Assert.True(claimed.IsClaimedBy("manager"));
    }

    /// <summary>Claim and release only go through their own methods, so the claim fields can never be skipped.</summary>
    [Fact]
    public void TransitionTo_cannot_claim_or_release()
    {
        var application = Submitted();
        Assert.Throws<InvalidOperationException>(() => application.TransitionTo(ApplicationStatus.UnderReview, "manager", Start));

        application.Claim("manager", Start);
        Assert.Throws<InvalidOperationException>(() => application.TransitionTo(ApplicationStatus.Submitted, "manager", Start));
    }

    /// <summary>A review outcome can be recorded only by the manager holding the claim.</summary>
    [Fact]
    public void Only_the_claim_owner_can_record_an_outcome()
    {
        var application = Submitted();
        application.Claim("manager", Start);

        Assert.Throws<InvalidOperationException>(() =>
            application.TransitionTo(ApplicationStatus.Denied, "other", Start, ReviewOutcome.Deny, "No."));
        Assert.Equal(ApplicationStatus.UnderReview, application.Status);

        application.TransitionTo(ApplicationStatus.Denied, "manager", Start, ReviewOutcome.Deny, "No.");
        Assert.Equal(ApplicationStatus.Denied, application.Status);
    }

    /// <summary>Every move out of Under Review clears the claim: an outcome, and the applicant withdrawing.</summary>
    [Theory]
    [InlineData(ReviewOutcome.Approve)]
    [InlineData(ReviewOutcome.Return)]
    [InlineData(ReviewOutcome.Deny)]
    [InlineData(null)]
    public void Leaving_under_review_clears_the_claim(ReviewOutcome? outcome)
    {
        var application = Submitted();
        application.Claim("manager", Start);

        if (outcome is { } o)
        {
            application.TransitionTo(o.ToStatus(), "manager", Start, o, "Reason.");
        }
        else
        {
            application.TransitionTo(ApplicationStatus.Withdrawn, "applicant", Start);
        }

        Assert.Null(application.ClaimedByUserId);
        Assert.Null(application.ClaimedAt);
    }

    /// <summary>Each review outcome leads to its status; Return and Deny need a comment, Approve doesn't.</summary>
    [Theory]
    [InlineData(ReviewOutcome.Approve, ApplicationStatus.Approved, false)]
    [InlineData(ReviewOutcome.Return, ApplicationStatus.Returned, true)]
    [InlineData(ReviewOutcome.Deny, ApplicationStatus.Denied, true)]
    public void Review_outcomes_map_to_statuses_and_comment_rules(ReviewOutcome outcome, ApplicationStatus status, bool needsComment)
    {
        Assert.Equal(status, outcome.ToStatus());
        Assert.Equal(needsComment, outcome.RequiresComment());
    }

    /// <summary>Statuses read as words: "Under Review" in labels, "under review" inside a sentence.</summary>
    [Fact]
    public void Status_display_names_are_words()
    {
        Assert.Equal("Under Review", ApplicationStatus.UnderReview.DisplayName());
        Assert.Equal("under review", ApplicationStatus.UnderReview.InSentence());
        Assert.Equal("Submitted", ApplicationStatus.Submitted.DisplayName());
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>A new application, submitted by "applicant".</summary>
    private static RentalApplication Submitted()
    {
        var application = RentalApplication.StartDraft(1, "applicant", Start);
        application.TransitionTo(ApplicationStatus.Submitted, "applicant", Start);
        return application;
    }

    /// <summary>An application driven through legal steps to <paramref name="status"/> (reviews by "manager").</summary>
    internal static RentalApplication InStatus(ApplicationStatus status)
    {
        var application = RentalApplication.StartDraft(1, "applicant", Start);
        if (status == ApplicationStatus.Draft)
        {
            return application;
        }

        if (status == ApplicationStatus.Withdrawn)
        {
            application.TransitionTo(ApplicationStatus.Withdrawn, "applicant", Start);
            return application;
        }

        application.TransitionTo(ApplicationStatus.Submitted, "applicant", Start);
        if (status == ApplicationStatus.Submitted)
        {
            return application;
        }

        application.Claim("manager", Start);
        var outcome = status switch
        {
            ApplicationStatus.Approved => ReviewOutcome.Approve,
            ApplicationStatus.Returned => ReviewOutcome.Return,
            ApplicationStatus.Denied => ReviewOutcome.Deny,
            _ => (ReviewOutcome?)null,
        };
        if (outcome is { } o)
        {
            application.TransitionTo(status, "manager", Start, o, "Reason.");
        }

        return application;
    }
}
