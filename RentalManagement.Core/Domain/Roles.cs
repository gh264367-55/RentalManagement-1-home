// =====================================================================================================================
// Module: RentalManagement.Core / Domain / Roles.cs
// Purpose: Names of the system's only two roles, as constants so authorization attributes, seeding and sign-up all use
//          the same spelling.
// =====================================================================================================================

namespace RentalManagement.Core.Domain;

/// <summary>The only two roles in the system. Use these constants in <c>[Authorize(Roles = …)]</c>.</summary>
public static class Roles
{
    public const string Applicant = "Applicant";

    public const string PropertyManager = "PropertyManager";

    /// <summary>Every valid role. Sign-up checks the chosen role against this list.</summary>
    public static readonly IReadOnlyList<string> All = [Applicant, PropertyManager];
}
