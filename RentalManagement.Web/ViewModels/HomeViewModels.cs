// =====================================================================================================================
// Module: RentalManagement.Web / ViewModels / HomeViewModels.cs
// Purpose: Static marketing content for the public landing page (Home/Index). Nothing here comes from the database,
//          so an anonymous visitor never sees a property, unit or application.
// =====================================================================================================================

namespace RentalManagement.Web.ViewModels;

/// <summary>One card on the landing page, rendered by the <c>_FeatureCard</c> partial.</summary>
/// <param name="Icon">The icon name understood by the <c>_Icon</c> partial.</param>
/// <param name="Title">The card heading.</param>
/// <param name="Text">One or two sentences describing the feature.</param>
/// <param name="Step">The step number for "How it works" cards; null for plain feature cards.</param>
public sealed record LandingFeature(string Icon, string Title, string Text, int? Step = null);

/// <summary>The fixed content of the public landing page.</summary>
public static class LandingContent
{
    /// <summary>The "How it works" steps for applicants, in order.</summary>
    public static IReadOnlyList<LandingFeature> Steps { get; } =
    [
        new("user-plus", "Create your account",
            "Sign up in a minute as an applicant. Your account keeps your details and applications in one secure place.", 1),
        new("search", "Browse available properties",
            "Once signed in, see the units that are free to rent today, with rent, bedrooms and unit type at a glance.", 2),
        new("clipboard-check", "Submit and track applications",
            "Apply online section by section, save drafts, and follow each application from submitted to decision.", 3),
    ];

    /// <summary>The highlights for property managers.</summary>
    public static IReadOnlyList<LandingFeature> ManagerFeatures { get; } =
    [
        new("building", "Property & unit management",
            "Maintain properties and units, set rent and unit types, and see which units are leased or available."),
        new("inbox", "Rental application processing",
            "Approve, return for correction or deny applications. Approval issues a 12-month lease automatically."),
        new("list-check", "Application review queue",
            "Work through submitted applications oldest first, with filters, sorting and paging."),
        new("hand", "Claim & release",
            "Claim an application so no one else reviews it at the same time, and release it back to the queue if needed."),
    ];
}
