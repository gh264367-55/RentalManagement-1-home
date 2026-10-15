// =====================================================================================================================
// Module: RentalManagement.Tests / Web / AuthorizationMatrixTests.cs
// Purpose: Guards the web layer's access rules by reflection over every controller action: the roles that can reach
//          each action (combining controller- and action-level [Authorize]/[AllowAnonymous], with the app's
//          sign-in-required fallback policy) must match an explicit expected table, so a new or changed endpoint can't
//          silently become reachable by the wrong role. Also checks that no POST opts out of anti-forgery validation
//          and that POST record ids are bound from the route only.
// =====================================================================================================================

using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using RentalManagement.Core.Domain;
using RentalManagement.Web.Controllers;

namespace RentalManagement.Tests.Web;

/// <summary>Reflection tests of the controllers' authorization, anti-forgery and binding attributes.</summary>
public class AuthorizationMatrixTests
{
    private const string Anonymous = "Anonymous";
    private const string AnySignedIn = "Applicant+PropertyManager";
    private const string Applicant = Roles.Applicant;
    private const string Manager = Roles.PropertyManager;

    /// <summary>Who may reach each action. Key: "Controller.Action VERB" (ANY when no HTTP verb attribute).</summary>
    private static readonly IReadOnlyDictionary<string, string> Expected = new Dictionary<string, string>
    {
        ["Account.Login GET"] = Anonymous,
        ["Account.Login POST"] = Anonymous,
        ["Account.Register GET"] = Anonymous,
        ["Account.Register POST"] = Anonymous,
        ["Account.Logout POST"] = Anonymous,
        ["Account.AccessDenied GET"] = Anonymous,
        ["Home.Index ANY"] = Anonymous,
        ["Home.Privacy ANY"] = Anonymous,
        ["Home.Status ANY"] = Anonymous,
        ["Home.Error ANY"] = Anonymous,

        ["Applications.Index GET"] = AnySignedIn,
        ["Applications.Edit GET"] = AnySignedIn,
        ["Applications.StatusPanel GET"] = AnySignedIn,
        ["Applications.Start POST"] = Applicant,
        ["Applications.Edit POST"] = Applicant,
        ["Applications.Withdraw GET"] = Applicant,
        ["Applications.Withdraw POST"] = Applicant,
        ["Applications.Review GET"] = Manager,
        ["Applications.Review POST"] = Manager,
        ["Applications.History GET"] = Manager,
        ["Applications.ReviewQueue GET"] = Manager,
        ["Applications.Claim POST"] = Manager,
        ["Applications.Release GET"] = Manager,
        ["Applications.Release POST"] = Manager,

        ["ApplicationsApi.Grid GET"] = AnySignedIn,
        ["ApplicationsApi.ReviewQueue GET"] = Manager,

        ["Residences.List GET"] = Applicant,
        ["Residences.Create GET"] = Applicant,
        ["Residences.Create POST"] = Applicant,
        ["Residences.Edit GET"] = Applicant,
        ["Residences.Edit POST"] = Applicant,
        ["Residences.Delete GET"] = Applicant,
        ["Residences.Delete POST"] = Applicant,

        ["Units.Available GET"] = Applicant,
        ["Units.ForProperty GET"] = Manager,
        ["Units.Create GET"] = Manager,
        ["Units.Create POST"] = Manager,
        ["Units.Edit GET"] = Manager,
        ["Units.Edit POST"] = Manager,
        ["Units.Delete GET"] = Manager,
        ["Units.Delete POST"] = Manager,

        ["Properties.Index GET"] = Manager,
        ["Properties.List GET"] = Manager,
        ["Properties.Summary GET"] = Manager,
        ["Properties.Create GET"] = Manager,
        ["Properties.Create POST"] = Manager,
        ["Properties.Edit GET"] = Manager,
        ["Properties.Edit POST"] = Manager,
        ["Properties.Delete GET"] = Manager,
        ["Properties.Delete POST"] = Manager,
    };

    /// <summary>Every action is in the expected table, and nothing in the table is stale.</summary>
    [Fact]
    public void Every_action_has_an_explicit_access_decision()
    {
        var actual = Actions().Select(a => a.Key).ToHashSet();

        Assert.Empty(actual.Except(Expected.Keys));   // a new action needs a decision in the table above
        Assert.Empty(Expected.Keys.Except(actual));   // a removed/renamed action must be removed from the table
    }

    /// <summary>Each action's effective access (attributes combined, fallback = any signed-in user) matches the table.</summary>
    [Fact]
    public void Each_action_is_reachable_only_by_the_expected_roles()
    {
        var mismatches = Actions()
            .Where(a => Expected.TryGetValue(a.Key, out var expected) && expected != EffectiveAccess(a.Controller, a.Method))
            .Select(a => $"{a.Key}: expected {Expected[a.Key]}, got {EffectiveAccess(a.Controller, a.Method)}")
            .ToList();

        Assert.Empty(mismatches);
    }

    /// <summary>No action or controller opts out of the global anti-forgery validation.</summary>
    [Fact]
    public void Nothing_opts_out_of_anti_forgery_validation()
    {
        var optedOut = Actions()
            .Where(a => a.Method.IsDefined(typeof(IgnoreAntiforgeryTokenAttribute), true)
                        || a.Controller.IsDefined(typeof(IgnoreAntiforgeryTokenAttribute), true))
            .Select(a => a.Key);

        Assert.Empty(optedOut);
    }

    /// <summary>
    /// POST actions read their record id from the route only. MVC binds simple parameters from form fields before
    /// route values, so without [FromRoute] a posted "id" field would retarget the action.
    /// </summary>
    [Fact]
    public void Post_actions_bind_record_ids_from_the_route_only()
    {
        var unprotected = Actions()
            .Where(a => a.Verb == "POST")
            .SelectMany(a => a.Method.GetParameters()
                .Where(p => p.Name == "id" && p.GetCustomAttribute<FromRouteAttribute>() is null)
                .Select(_ => a.Key));

        Assert.Empty(unprotected);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>An action method with its controller, HTTP verb and table key.</summary>
    private sealed record ActionInfo(Type Controller, MethodInfo Method, string Verb)
    {
        public string Key => $"{Controller.Name[..^"Controller".Length]}.{Method.GetCustomAttribute<ActionNameAttribute>()?.Name ?? Method.Name} {Verb}";
    }

    /// <summary>Every public action method declared on the Web project's controllers (MVC and API, i.e. every ControllerBase).</summary>
    private static IEnumerable<ActionInfo> Actions() =>
        typeof(HomeController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && !m.IsDefined(typeof(NonActionAttribute)))
                .Select(m => new ActionInfo(t, m, m.GetCustomAttributes<HttpMethodAttribute>()
                    .SelectMany(h => h.HttpMethods).FirstOrDefault() ?? "ANY")));

    /// <summary>
    /// The roles that can reach an action: AllowAnonymous wins; otherwise every [Authorize] on the controller and
    /// the action must be satisfied (they are ANDed), and with no roles named the fallback policy admits any
    /// signed-in user (both roles).
    /// </summary>
    private static string EffectiveAccess(Type controller, MethodInfo method)
    {
        if (method.IsDefined(typeof(AllowAnonymousAttribute), true) || controller.IsDefined(typeof(AllowAnonymousAttribute), true))
        {
            return Anonymous;
        }

        var roles = new HashSet<string>(Roles.All);
        foreach (var attribute in controller.GetCustomAttributes<AuthorizeAttribute>(true).Concat(method.GetCustomAttributes<AuthorizeAttribute>(true)))
        {
            if (!string.IsNullOrWhiteSpace(attribute.Roles))
            {
                roles.IntersectWith(attribute.Roles.Split(',', StringSplitOptions.TrimEntries));
            }
        }

        return roles.Count switch
        {
            0 => "Nobody",
            1 => roles.Single(),
            _ => AnySignedIn,
        };
    }
}
