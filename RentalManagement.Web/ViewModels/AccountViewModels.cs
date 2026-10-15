// =====================================================================================================================
// Module: RentalManagement.Web / ViewModels / AccountViewModels.cs
// Purpose: Form models of the login and sign-up pages, with the data annotations that drive both client-side and
//          server-side validation of those forms.
// =====================================================================================================================

using System.ComponentModel.DataAnnotations;
using RentalManagement.Core.Domain;

namespace RentalManagement.Web.ViewModels;

/// <summary>The login form.</summary>
public sealed class LoginViewModel
{
    /// <summary>The account's email address (also its user name).</summary>
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>The account password.</summary>
    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    /// <summary>Keep the user signed in across browser sessions.</summary>
    [Display(Name = "Remember me")]
    public bool RememberMe { get; set; }

    /// <summary>The page to return to after signing in; honoured only when it is a local URL.</summary>
    public string? ReturnUrl { get; set; }
}

/// <summary>The sign-up form, including the choice of role.</summary>
public sealed class RegisterViewModel
{
    /// <summary>The user's first name.</summary>
    [Required, StringLength(100), Display(Name = "First name")]
    public string FirstName { get; set; } = string.Empty;

    /// <summary>The user's last name.</summary>
    [Required, StringLength(100), Display(Name = "Last name")]
    public string LastName { get; set; } = string.Empty;

    /// <summary>The email address, used as the user name.</summary>
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>The new password; Identity's password rules are applied on top of the length check.</summary>
    [Required, StringLength(100, MinimumLength = 8), DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    /// <summary>Must match <see cref="Password"/>.</summary>
    [Required, DataType(DataType.Password), Display(Name = "Confirm password")]
    [Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>Applicant or PropertyManager. The controller checks it against <see cref="Roles.All"/>.</summary>
    [Required(ErrorMessage = "Choose whether you are an applicant or a property manager."), Display(Name = "I am")]
    public string Role { get; set; } = Roles.Applicant;
}
