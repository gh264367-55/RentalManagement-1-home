// =====================================================================================================================
// Module: RentalManagement.Core / Domain / ApplicationUser.cs
// Purpose: The ASP.NET Core Identity user for both roles (Applicant and PropertyManager), extended with a first and
//          last name for display on applications, reviews and the status history.
// =====================================================================================================================

using Microsoft.AspNetCore.Identity;

namespace RentalManagement.Core.Domain;

/// <summary>An Identity user (applicant or property manager) with a display name.</summary>
public class ApplicationUser : IdentityUser
{
    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    public string FullName => $"{FirstName} {LastName}".Trim();
}
