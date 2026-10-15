// =====================================================================================================================
// Module: RentalManagement.Core / Data / Seeding / ApplicationScenarioBuilder.cs
// Purpose: A small fluent builder the seeder uses to script each demo application ("draft 10 days ago, complete,
//          submit 8 days ago, approve, issue lease"). It calls the real domain methods so seeded histories always obey
//          the state machine, and uses Bogus for realistic addresses, landlords and residence dates.
// =====================================================================================================================

using Bogus;
using RentalManagement.Core.Domain;

namespace RentalManagement.Core.Data.Seeding;

/// <summary>
/// Builds seeded applications step by step through the real domain methods (<see cref="RentalApplication.StartDraft"/>,
/// <see cref="RentalApplication.TransitionTo"/>, <see cref="Lease.Issue"/>), so every seeded status has a legal,
/// realistic history. Times are given as "days ago" relative to the injected clock.
/// </summary>
internal sealed class ApplicationScenarioBuilder(Faker faker, TimeProvider clock)
{
    private readonly Faker _faker = faker;
    private readonly DateTimeOffset _now = clock.GetUtcNow();
    private readonly DateOnly _today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    /// <summary>Starts a scenario: a new draft for <paramref name="unit"/> created <paramref name="daysAgo"/> days ago.</summary>
    /// <param name="unit">The (already saved) unit applied for.</param>
    /// <param name="applicant">The applicant who owns the application.</param>
    /// <param name="daysAgo">How many days before "now" the draft was created.</param>
    /// <returns>The scenario, ready for further steps.</returns>
    public Scenario Draft(Unit unit, ApplicationUser applicant, int daysAgo) =>
        new(this, unit, applicant, RentalApplication.StartDraft(unit.Id, applicant.Id, At(daysAgo)));

    /// <summary>The instant <paramref name="daysAgo"/> days before the clock's "now".</summary>
    private DateTimeOffset At(int daysAgo) => _now.AddDays(-daysAgo);

    /// <summary>One application being built. Each method returns the same scenario so steps read in order.</summary>
    internal sealed class Scenario(
        ApplicationScenarioBuilder owner, Unit unit, ApplicationUser applicant, RentalApplication application)
    {
        private int? _approvedDaysAgo;

        /// <summary>Saves section 1 with the applicant's own name and email.</summary>
        public Scenario WithApplicantInfo()
        {
            application.FirstName = applicant.FirstName;
            application.LastName = applicant.LastName;
            application.Email = applicant.Email;
            application.Phone = applicant.PhoneNumber;
            application.CurrentAddress = DatabaseSeeder.NewAddress(owner._faker);
            application.ApplicantInfoCompleted = true;
            return this;
        }

        /// <summary>Saves section 2 with 1–3 consecutive prior residences ending shortly before the draft was created.</summary>
        public Scenario WithResidences()
        {
            var f = owner._faker;
            var moveOut = DateOnly.FromDateTime(application.CreatedAt.UtcDateTime).AddDays(-f.Random.Int(0, 30));
            var count = f.Random.Int(1, 3);
            for (var i = 0; i < count; i++)
            {
                var moveIn = moveOut.AddMonths(-f.Random.Int(10, 48));
                application.Residences.Add(new Residence
                {
                    Address = DatabaseSeeder.NewAddress(f),
                    LandlordName = f.Name.FullName(),
                    LandlordPhone = f.Phone.PhoneNumber("(###) ###-####"),
                    MoveInDate = moveIn,
                    MoveOutDate = moveOut,
                });
                moveOut = moveIn.AddDays(-1);
            }

            application.ResidenceHistoryCompleted = true;
            return this;
        }

        /// <summary>Both sections saved.</summary>
        public Scenario Complete() => WithApplicantInfo().WithResidences();

        /// <summary>The applicant submits (or resubmits) the application <paramref name="daysAgo"/> days ago.</summary>
        /// <param name="daysAgo">Days before "now".</param>
        /// <returns>This scenario.</returns>
        public Scenario Submit(int daysAgo)
        {
            application.TransitionTo(ApplicationStatus.Submitted, applicant.Id, owner.At(daysAgo));
            return this;
        }

        /// <summary>
        /// A property manager records a review outcome <paramref name="daysAgo"/> days ago. An approval is remembered
        /// so <see cref="WithLease"/> can start the lease on the approval day.
        /// </summary>
        /// <param name="manager">The reviewing property manager.</param>
        /// <param name="outcome">Approve, Return or Deny.</param>
        /// <param name="daysAgo">Days before "now".</param>
        /// <param name="comment">The reviewer's comment (expected for Return and Deny).</param>
        /// <returns>This scenario.</returns>
        public Scenario Review(ApplicationUser manager, ReviewOutcome outcome, int daysAgo, string? comment = null)
        {
            // As in the app, a review needs the manager's claim; claim a few hours before deciding if not done already.
            if (!application.IsClaimedBy(manager.Id))
            {
                application.Claim(manager.Id, owner.At(daysAgo).AddHours(-3));
            }

            application.TransitionTo(outcome.ToStatus(), manager.Id, owner.At(daysAgo), outcome, comment);
            if (outcome == ReviewOutcome.Approve)
            {
                _approvedDaysAgo = daysAgo;
            }

            return this;
        }

        /// <summary>
        /// A property manager claims the submitted application for review (Submitted → Under Review), at
        /// <paramref name="daysAgo"/> days plus <paramref name="hoursAgo"/> hours before now.
        /// </summary>
        /// <param name="manager">The claiming property manager.</param>
        /// <param name="daysAgo">Days before "now".</param>
        /// <param name="hoursAgo">Extra hours before that, for a realistic time of day.</param>
        /// <returns>This scenario.</returns>
        public Scenario Claim(ApplicationUser manager, int daysAgo, int hoursAgo = 0)
        {
            application.Claim(manager.Id, owner.At(daysAgo).AddHours(-hoursAgo));
            return this;
        }

        /// <summary>The claiming manager releases the application back to the queue <paramref name="daysAgo"/> days ago.</summary>
        /// <param name="manager">The manager who holds the claim.</param>
        /// <param name="daysAgo">Days before "now".</param>
        /// <returns>This scenario.</returns>
        public Scenario Release(ApplicationUser manager, int daysAgo)
        {
            application.Release(manager.Id, owner.At(daysAgo));
            return this;
        }

        /// <summary>The applicant withdraws the application <paramref name="daysAgo"/> days ago.</summary>
        /// <param name="daysAgo">Days before "now".</param>
        /// <returns>This scenario.</returns>
        public Scenario Withdraw(int daysAgo)
        {
            application.TransitionTo(ApplicationStatus.Withdrawn, applicant.Id, owner.At(daysAgo));
            return this;
        }

        /// <summary>Issues the twelve-month lease starting on the approval day, as approval does in the app.</summary>
        /// <returns>This scenario.</returns>
        /// <exception cref="InvalidOperationException">No Approve review was recorded first.</exception>
        public Scenario WithLease()
        {
            if (_approvedDaysAgo is not { } daysAgo)
            {
                throw new InvalidOperationException("WithLease() must follow an Approve review.");
            }

            application.Lease = Lease.Issue(
                application, unit.MonthlyRent, owner._today.AddDays(-daysAgo), owner.At(daysAgo));
            unit.ConcurrencyStamp = Guid.NewGuid();
            return this;
        }

        /// <summary>Finishes the scenario.</summary>
        /// <returns>The built (not yet saved) application.</returns>
        public RentalApplication Build() => application;
    }
}
