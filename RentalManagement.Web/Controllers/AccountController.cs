// =====================================================================================================================
// Module: RentalManagement.Web / Controllers / AccountController.cs
// Purpose: Account pages built on ASP.NET Core Identity: sign up (choosing Applicant or Property Manager), log in
//          with lockout protection, POST-only log out, and the access-denied page. After signing in, users go to a
//          local return URL or to their role's start page.
// =====================================================================================================================

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RentalManagement.Core.Domain;
using RentalManagement.Web.ViewModels;

namespace RentalManagement.Web.Controllers;

/// <summary>Sign up (choosing a role), log in and log out.</summary>
[AllowAnonymous]
public sealed class AccountController(
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    ILogger<AccountController> logger) : Controller
{
    /// <summary>Shows the login form.</summary>
    /// <param name="returnUrl">The page to return to after signing in (only local URLs are honoured).</param>
    /// <returns>The login view.</returns>
    [HttpGet]
    public IActionResult Login(string? returnUrl = null) => View(new LoginViewModel { ReturnUrl = returnUrl });

    /// <summary>
    /// Signs the user in with email and password. Failed attempts count towards lockout; a single generic message is
    /// shown for an unknown email or a wrong password so accounts can't be probed.
    /// </summary>
    /// <param name="model">The posted credentials.</param>
    /// <returns>A redirect on success; the form with an error otherwise.</returns>
    [HttpPost]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await signInManager.PasswordSignInAsync(model.Email, model.Password, model.RememberMe, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, "This account is locked after too many failed attempts. Try again in a few minutes.");
            return View(model);
        }

        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, "Invalid email or password.");
            return View(model);
        }

        var user = await userManager.FindByEmailAsync(model.Email);
        return await RedirectAfterSignInAsync(user!, model.ReturnUrl);
    }

    /// <summary>Shows the sign-up form (Applicant is pre-selected).</summary>
    /// <returns>The sign-up view.</returns>
    [HttpGet]
    public IActionResult Register() => View(new RegisterViewModel());

    /// <summary>
    /// Creates the account, adds it to the chosen role (checked against <see cref="Roles.All"/>), signs the user in
    /// and sends them to their role's start page. Identity's errors are shown next to the matching field.
    /// </summary>
    /// <param name="model">The posted sign-up form.</param>
    /// <returns>A redirect on success; the form with errors otherwise.</returns>
    [HttpPost]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        // The role comes from a radio button the browser could change, so only the two real roles are accepted.
        if (!Roles.All.Contains(model.Role))
        {
            ModelState.AddModelError(nameof(model.Role), "Choose whether you are an applicant or a property manager.");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = new ApplicationUser
        {
            UserName = model.Email,
            Email = model.Email,
            FirstName = model.FirstName.Trim(),
            LastName = model.LastName.Trim(),
        };

        var created = await userManager.CreateAsync(user, model.Password);
        if (!created.Succeeded)
        {
            foreach (var error in created.Errors)
            {
                var field = error.Code.Contains("Password", StringComparison.Ordinal) ? nameof(model.Password)
                    : error.Code.Contains("Email", StringComparison.Ordinal) || error.Code.Contains("UserName", StringComparison.Ordinal) ? nameof(model.Email)
                    : string.Empty;
                ModelState.AddModelError(field, error.Description);
            }

            return View(model);
        }

        await userManager.AddToRoleAsync(user, model.Role);
        logger.LogInformation("New {Role} registered: {Email}", model.Role, model.Email);

        await signInManager.SignInAsync(user, isPersistent: false);
        return await RedirectAfterSignInAsync(user, returnUrl: null);
    }

    /// <summary>Signs the user out. POST-only (with anti-forgery), so a link or image can't log someone out.</summary>
    /// <returns>A redirect to the home page.</returns>
    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return RedirectToAction(nameof(HomeController.Index), "Home");
    }

    /// <summary>Shown when a signed-in user opens a page their role may not use.</summary>
    /// <returns>The access-denied view.</returns>
    [HttpGet]
    public IActionResult AccessDenied() => View();

    /// <summary>Goes back to a local return URL, otherwise to the role's start page. Never to an external URL.</summary>
    /// <param name="user">The user who just signed in.</param>
    /// <param name="returnUrl">The requested return URL, if any.</param>
    /// <returns>The redirect.</returns>
    private async Task<IActionResult> RedirectAfterSignInAsync(ApplicationUser user, string? returnUrl)
    {
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return await userManager.IsInRoleAsync(user, Roles.PropertyManager)
            ? RedirectToAction(nameof(PropertiesController.Index), "Properties")
            : RedirectToAction(nameof(UnitsController.Available), "Units");
    }
}
