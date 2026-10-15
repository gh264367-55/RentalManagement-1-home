using Bogus;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services;

namespace RentalManagement.Core.Data.Seeding;

/// <summary>
/// Seeds demo data using Bogus for development and testing.
/// Checks for existing records to prevent duplicate data.
/// Uses fixed seeds for consistent sample data.
/// Uses TimeProvider to generate dates relative to today.
/// </summary>
public sealed class DatabaseSeeder(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    TimeProvider clock,
    IOptions<SeedOptions> options,
    ILogger<DatabaseSeeder> logger)
{
    /// <summary>Number of seeded property managers (manager1…manager2@rental.test).</summary>
    public const int ManagerCount = 2;

    /// <summary>Number of seeded applicants (applicant1…applicant8@rental.test).</summary>
    public const int ApplicantCount = 8;

    /// <summary>The seeded unit type that is inactive but still used by one unit.</summary>
    public const string InactiveUnitTypeName = "Garden Suite";

    private const int BaseSeed = 20261008;
    private const string PhoneFormat = "(###) ###-####";

    /// <summary>The Unit Type lookup values, in display order; the last one is inactive on purpose.</summary>
    private static readonly (string Name, bool IsActive)[] UnitTypeDefinitions =
    [
        ("Studio", true),
        ("Apartment", true),
        ("Loft", true),
        ("Townhouse", true),
        (InactiveUnitTypeName, false),
    ];

    /// <summary>One seeded property per suffix, named "{Bogus street name} {suffix}".</summary>
    private static readonly string[] PropertySuffixes = ["Apartments", "Residences", "Commons", "Lofts", "Terrace"];

    /// <summary>
    /// Runs every seeding step in dependency order: roles, users, unit types, properties with units, applications.
    /// </summary>
    /// <param name="cancellationToken">Cancels the database work.</param>
    /// <returns>A task that completes when all steps have run.</returns>
    /// <exception cref="InvalidOperationException">The seed password is missing or Identity rejected a user or role.</exception>
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await SeedRolesAsync();
        var managers = await SeedUsersAsync(Roles.PropertyManager, "manager", ManagerCount, BaseSeed + 1);
        var applicants = await SeedUsersAsync(Roles.Applicant, "applicant", ApplicantCount, BaseSeed + 2);
        await SeedUnitTypesAsync(cancellationToken);
        await SeedPropertiesAsync(cancellationToken);
        await SeedApplicationsAsync(managers, applicants, cancellationToken);
    }

    /// <summary>Creates each role in <see cref="Roles.All"/> that doesn't exist yet.</summary>
    private async Task SeedRolesAsync()
    {
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                EnsureSucceeded(await roleManager.CreateAsync(new IdentityRole(role)), $"create role {role}");
                logger.LogInformation("Seeded role {Role}", role);
            }
        }
    }

    /// <summary>
    /// Ensures <paramref name="count"/> users named <c>{emailPrefix}{n}@rental.test</c> exist in <paramref name="role"/>,
    /// creating any that are missing with Bogus names and phone numbers and the configured password.
    /// </summary>
    /// <param name="role">The role to put the users in.</param>
    /// <param name="emailPrefix">The email prefix, e.g. "manager".</param>
    /// <param name="count">How many users to ensure.</param>
    /// <param name="seed">Bogus seed, so the same names are generated every time.</param>
    /// <returns>The users, existing or new, in order.</returns>
    private async Task<IReadOnlyList<ApplicationUser>> SeedUsersAsync(string role, string emailPrefix, int count, int seed)
    {
        var password = options.Value.DefaultPassword;
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                $"{SeedOptions.SectionName}:{nameof(SeedOptions.DefaultPassword)} is not configured. " +
                "Set it in appsettings.Development.json or user secrets, or disable seeding with Seed:Enabled=false.");
        }

        var faker = NewFaker(seed);
        var users = new List<ApplicationUser>(count);
        for (var i = 1; i <= count; i++)
        {
            // Always draw the fake values, even for an existing user, so later users get the same data every run.
            var firstName = faker.Name.FirstName();
            var lastName = faker.Name.LastName();
            var phone = faker.Phone.PhoneNumber(PhoneFormat);
            var email = $"{emailPrefix}{i}@rental.test";

            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FirstName = firstName,
                    LastName = lastName,
                    PhoneNumber = phone,
                };
                EnsureSucceeded(await userManager.CreateAsync(user, password), $"create user {email}");
                logger.LogInformation("Seeded {Role} {Email}", role, email);
            }

            if (!await userManager.IsInRoleAsync(user, role))
            {
                EnsureSucceeded(await userManager.AddToRoleAsync(user, role), $"add {email} to {role}");
            }

            users.Add(user);
        }

        return users;
    }

    /// <summary>Adds each unit type from the built-in list whose name isn't in the lookup yet.</summary>
    /// <param name="cancellationToken">Cancels the database work.</param>
    private async Task SeedUnitTypesAsync(CancellationToken cancellationToken)
    {
        var existing = await db.UnitTypes.Select(t => t.Name).ToListAsync(cancellationToken);
        var missing = UnitTypeDefinitions
            .Select((d, index) => new UnitType { Name = d.Name, IsActive = d.IsActive, SortOrder = index + 1 })
            .Where(t => !existing.Contains(t.Name))
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }

        db.UnitTypes.AddRange(missing);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded {Count} unit types", missing.Count);
    }

    /// <summary>
    /// Creates five properties with 4–8 units each (only when no property exists yet). Units get active types, with
    /// one unit deliberately left on the inactive type.
    /// </summary>
    /// <param name="cancellationToken">Cancels the database work.</param>
    private async Task SeedPropertiesAsync(CancellationToken cancellationToken)
    {
        if (await db.Properties.AnyAsync(cancellationToken))
        {
            return;
        }

        var types = await db.UnitTypes.ToDictionaryAsync(t => t.Name, cancellationToken);
        var activeTypes = types.Values.Where(t => t.IsActive).OrderBy(t => t.SortOrder).ToArray();
        var faker = NewFaker(BaseSeed + 3);

        var properties = new List<Property>();
        foreach (var suffix in PropertySuffixes)
        {
            var property = new Property
            {
                Name = $"{faker.Address.StreetName()} {suffix}",
                Address = NewAddress(faker),
            };

            var unitCount = faker.Random.Int(4, 8);
            for (var i = 0; i < unitCount; i++)
            {
                var type = faker.PickRandom(activeTypes);
                var bedrooms = BedroomsFor(type.Name, faker);
                property.Units.Add(new Unit
                {
                    // Four units per floor: 101, 102, 103, 104, 201, …
                    UnitNumber = $"{i / 4 + 1}{i % 4 + 1:00}",
                    Bedrooms = bedrooms,
                    MonthlyRent = 900m + bedrooms * 350m + faker.Random.Int(0, 12) * 25m,
                    UnitType = type,
                });
            }

            properties.Add(property);
        }

        // One unit keeps the inactive type, to show that it still displays where it is already used.
        var legacyUnit = properties[0].Units.Last();
        legacyUnit.UnitType = types[InactiveUnitTypeName];
        legacyUnit.Bedrooms = 1;

        db.Properties.AddRange(properties);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Seeded {Properties} properties with {Units} units", properties.Count, properties.Sum(p => p.Units.Count));
    }

    /// <summary>
    /// Creates fourteen applications, two in every status, each scripted through <see cref="ApplicationScenarioBuilder"/>
    /// (only when no application exists yet). Includes two submitted applications for the same unit (one of them
    /// claimed and then released), two applications under review (one claimed by each seeded manager), one active and
    /// one expired lease, and returns/denials with reviewer comments. A database seeded before Under Review existed
    /// gets the two Under Review applications once (see <see cref="SeedUnderReviewTopUpAsync"/>).
    /// </summary>
    /// <param name="managers">Seeded property managers (reviewers).</param>
    /// <param name="applicants">Seeded applicants (owners).</param>
    /// <param name="cancellationToken">Cancels the database work.</param>
    /// <exception cref="InvalidOperationException">Fewer than 13 units exist.</exception>
    private async Task SeedApplicationsAsync(
        IReadOnlyList<ApplicationUser> managers, IReadOnlyList<ApplicationUser> applicants, CancellationToken cancellationToken)
    {
        if (await db.RentalApplications.AnyAsync(cancellationToken))
        {
            await SeedUnderReviewTopUpAsync(managers, applicants, cancellationToken);
            return;
        }

        var units = await db.Units
            .OrderBy(u => u.PropertyId).ThenBy(u => u.UnitNumber)
            .ToListAsync(cancellationToken);
        const int unitsNeeded = 13;
        if (units.Count < unitsNeeded)
        {
            throw new InvalidOperationException(
                $"Seeding applications needs at least {unitsNeeded} units, but the database has {units.Count}.");
        }

        var builder = new ApplicationScenarioBuilder(NewFaker(BaseSeed + 4), clock);
        var (m1, m2) = (managers[0], managers[1]);
        var a = applicants;

        RentalApplication[] applications =
        [
            // Draft: only Applicant Information saved so far.
            builder.Draft(units[0], a[0], daysAgo: 3).WithApplicantInfo().Build(),
            // Draft: both sections saved, ready to submit.
            builder.Draft(units[1], a[1], daysAgo: 5).WithApplicantInfo().WithResidences().Build(),

            // Submitted: two applicants for the SAME unit, so approving one blocks approving the other. The second one
            // was claimed and then released back to the queue, so its history shows both.
            builder.Draft(units[2], a[2], daysAgo: 10).Complete().Submit(daysAgo: 8).Build(),
            builder.Draft(units[2], a[3], daysAgo: 9).Complete().Submit(daysAgo: 6)
                .Claim(m2, daysAgo: 4, hoursAgo: 5).Release(m2, daysAgo: 4).Build(),

            // Returned: once, and twice (returned, corrected, resubmitted, returned again).
            builder.Draft(units[3], a[4], daysAgo: 20).Complete().Submit(daysAgo: 18)
                .Review(m1, ReviewOutcome.Return, daysAgo: 15,
                    "Please add the landlord's phone number for your most recent residence.").Build(),
            builder.Draft(units[4], a[5], daysAgo: 30).Complete().Submit(daysAgo: 28)
                .Review(m2, ReviewOutcome.Return, daysAgo: 25, "Your move-in dates overlap. Please correct them.")
                .Submit(daysAgo: 22)
                .Review(m1, ReviewOutcome.Return, daysAgo: 20, "Please list every residence for the last two years.").Build(),

            // Approved: one lease still active (unit unavailable), one lease already expired (unit available again).
            builder.Draft(units[5], a[6], daysAgo: 70).Complete().Submit(daysAgo: 65)
                .Review(m1, ReviewOutcome.Approve, daysAgo: 60, "Welcome home!").WithLease().Build(),
            builder.Draft(units[6], a[7], daysAgo: 440).Complete().Submit(daysAgo: 435)
                .Review(m2, ReviewOutcome.Approve, daysAgo: 430).WithLease().Build(),

            // Denied: directly, and after a return and resubmission.
            builder.Draft(units[7], a[0], daysAgo: 40).Complete().Submit(daysAgo: 38)
                .Review(m1, ReviewOutcome.Deny, daysAgo: 35, "Rental references could not be verified.").Build(),
            builder.Draft(units[8], a[1], daysAgo: 50).Complete().Submit(daysAgo: 48)
                .Review(m2, ReviewOutcome.Return, daysAgo: 45, "Please provide your current landlord's contact details.")
                .Submit(daysAgo: 43)
                .Review(m2, ReviewOutcome.Deny, daysAgo: 40, "The unit's occupancy limit is exceeded.").Build(),

            // Withdrawn: from Draft, and from Submitted.
            builder.Draft(units[9], a[2], daysAgo: 15).WithApplicantInfo().Withdraw(daysAgo: 12).Build(),
            builder.Draft(units[10], a[3], daysAgo: 25).Complete().Submit(daysAgo: 23).Withdraw(daysAgo: 21).Build(),

            // Under Review: one claimed by each manager.
            .. UnderReviewScenarios(units[11], units[12], m1, m2, a),
        ];

        db.RentalApplications.AddRange(applications);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded {Count} rental applications", applications.Length);
    }

    /// <summary>
    /// For a database seeded before the Under Review status existed: adds the two Under Review applications once, on
    /// two units that have no application and no lease covering today. "Once" is decided by the status history: as
    /// soon as any application has ever been claimed (seeded or by a real manager), this step does nothing, so later
    /// starts never add more, even after those claims were released or reviewed.
    /// </summary>
    /// <param name="managers">Seeded property managers (claim owners).</param>
    /// <param name="applicants">Seeded applicants (owners).</param>
    /// <param name="cancellationToken">Cancels the database work.</param>
    private async Task SeedUnderReviewTopUpAsync(
        IReadOnlyList<ApplicationUser> managers, IReadOnlyList<ApplicationUser> applicants, CancellationToken cancellationToken)
    {
        if (await db.ApplicationStatusHistory.AnyAsync(h => h.ToStatus == ApplicationStatus.UnderReview, cancellationToken))
        {
            return;
        }

        var today = clock.GetToday();
        var units = await db.Units
            .Where(u => !u.Applications.Any() && !u.Leases.Any(l => l.StartDate <= today && l.EndDate >= today))
            .OrderBy(u => u.PropertyId).ThenBy(u => u.UnitNumber)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (units.Count < 2)
        {
            logger.LogWarning("Skipped seeding Under Review applications: fewer than two units without applications.");
            return;
        }

        var applications = UnderReviewScenarios(units[0], units[1], managers[0], managers[1], applicants);
        db.RentalApplications.AddRange(applications);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded {Count} Under Review applications into existing data", applications.Length);
    }

    /// <summary>
    /// Two applications Under Review: one claimed by each manager (a day ago, and a few hours ago), so each manager sees
    /// one under "My claimed applications" and can see that the other belongs to a colleague.
    /// </summary>
    private RentalApplication[] UnderReviewScenarios(
        Unit first, Unit second, ApplicationUser m1, ApplicationUser m2, IReadOnlyList<ApplicationUser> a)
    {
        var builder = new ApplicationScenarioBuilder(NewFaker(BaseSeed + 5), clock);
        return
        [
            builder.Draft(first, a[4], daysAgo: 7).Complete().Submit(daysAgo: 5).Claim(m1, daysAgo: 1, hoursAgo: 2).Build(),
            builder.Draft(second, a[5], daysAgo: 6).Complete().Submit(daysAgo: 4).Claim(m2, daysAgo: 0, hoursAgo: 3).Build(),
        ];
    }

    /// <summary>A plausible bedroom count for a unit type (0 for a studio).</summary>
    /// <param name="unitTypeName">The unit type's name.</param>
    /// <param name="faker">The seeded faker supplying the random number.</param>
    /// <returns>The number of bedrooms.</returns>
    private static int BedroomsFor(string unitTypeName, Faker faker) => unitTypeName switch
    {
        "Studio" => 0,
        "Loft" => faker.Random.Int(1, 2),
        "Townhouse" => faker.Random.Int(2, 4),
        _ => faker.Random.Int(1, 3),
    };

    /// <summary>An English Bogus faker with its own fixed seed, so each seeding step is reproducible on its own.</summary>
    /// <param name="seed">The random seed.</param>
    /// <returns>A new faker.</returns>
    internal static Faker NewFaker(int seed) => new("en") { Random = new Randomizer(seed) };

    /// <summary>A fake US postal address.</summary>
    /// <param name="faker">The faker to draw values from.</param>
    /// <returns>A new address.</returns>
    internal static Address NewAddress(Faker faker) => new()
    {
        Street = faker.Address.StreetAddress(),
        City = faker.Address.City(),
        State = faker.Address.StateAbbr(),
        PostalCode = faker.Address.ZipCode("#####"),
    };

    /// <summary>Turns a failed Identity operation into an exception, since seeding can't continue without it.</summary>
    /// <param name="result">The Identity result to check.</param>
    /// <param name="action">What was attempted, for the error message.</param>
    /// <exception cref="InvalidOperationException">The operation failed.</exception>
    private static void EnsureSucceeded(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Seeding failed to {action}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }
    }
}
