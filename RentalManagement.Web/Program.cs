using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalManagement.Core;
using RentalManagement.Core.Data;
using RentalManagement.Core.Data.Seeding;
using RentalManagement.Core.Domain;
using RentalManagement.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// ---- Database -------------------------------------------------------------------------------------------------
// The connection string comes from configuration: appsettings.Development.json (LocalDB default), overridden per
// machine by user secrets, or by environment variables / a secret store outside development.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "Connection string 'DefaultConnection' is not configured. See README.md → Local SQL Server configuration.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString, sql =>
    {
        // ApplicationDbContext and its migrations live in RentalManagement.Core, not in this startup project.
        sql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.GetName().Name);
        sql.EnableRetryOnFailure();
    }));

// ---- Identity -------------------------------------------------------------------------------------------------
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";

    // Modal and region requests (fetch) get a status code instead of a redirect to an HTML page,
    // so the script can reload the page (401) or show "not allowed" (403).
    options.Events.OnRedirectToLogin = context => AjaxAwareRedirect(context, StatusCodes.Status401Unauthorized);
    options.Events.OnRedirectToAccessDenied = context => AjaxAwareRedirect(context, StatusCodes.Status403Forbidden);
});

// Every endpoint requires a signed-in user unless it opts out with [AllowAnonymous] (Home, Account, static files).
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// ---- Application services ---------------------------------------------------------------------------------------
builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection(SeedOptions.SectionName));
builder.Services.AddRentalManagementCore();
builder.Services.AddControllersWithViews(options =>
{
    // Every POST, including modal fetch posts, must carry the anti-forgery token.
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
});

// OpenAPI document of the JSON API (served in Development only, see below).
builder.Services.AddRentalManagementOpenApi();

var app = builder.Build();

// Create the database, apply migrations and seed before the app accepts any request.
await app.Services.InitializeDatabaseAsync();

// Development shows the detailed exception page; every other environment uses the friendly error page and HSTS.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. See https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// Error statuses without a body (404 for a record the user can't see, 400 for a failed anti-forgery check, …) get
// a friendly page instead of a blank one. Responses that already have a body (modal partials with 422/409) are
// left as they are, so the modal contract in site.js is unaffected.
// The code goes in the query string so it binds to HomeController.Status(int code).
app.UseStatusCodePagesWithReExecute("/Home/Status", "?code={0}");

// The JSON API answers with bare status codes or problem details, never with the HTML status page.
app.Use(async (context, next) =>
{
    if (context.Request.IsApiRequest() && context.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
    {
        statusCodePages.Enabled = false;
    }

    await next();
});

app.UseHttpsRedirection();

// Baseline security headers on every response: no MIME sniffing, no framing by any site (clickjacking; antiforgery
// would otherwise add only SAMEORIGIN, and only on pages that render a form), and no full URLs leaked as referrers.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

// One culture for money and dates, and for parsing posted numbers, whatever the server's or browser's locale is.
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture("en-US")
    .AddSupportedCultures("en-US")
    .AddSupportedUICultures("en-US"));

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Static files from wwwroot (Bootstrap, site.css, site.js), fingerprinted and compressed at build time.
app.MapStaticAssets().AllowAnonymous();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

// Development only: the OpenAPI document (/openapi/v1.json, anonymous so tools can read it) and a Swagger UI that
// renders it (/swagger). The UI is covered by the sign-in fallback policy: it redirects to the login page and back, and
// its "Try it out" calls then carry the sign-in cookie the API needs.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint($"/openapi/{OpenApiConfiguration.DocumentName}.json", "RentalManagement API v1");
        options.DocumentTitle = "RentalManagement API";
    });
}

await app.RunAsync();

// Cookie-authentication redirect handler. A normal browser request is redirected to the login or access-denied page as
// usual; a fetch request from site.js (X-Requested-With header) or any call to the JSON API (/api/...) gets a bare
// status code instead, so the script can reload the page (401) or show "not allowed" (403) rather than putting a login
// page inside a modal, and API clients get 401/403 instead of a redirect to HTML.
static Task AjaxAwareRedirect(RedirectContext<CookieAuthenticationOptions> context, int ajaxStatusCode)
{
    if (context.Request.IsAjaxRequest() || context.Request.IsApiRequest())
    {
        context.Response.StatusCode = ajaxStatusCode;
    }
    else
    {
        context.Response.Redirect(context.RedirectUri);
    }

    return Task.CompletedTask;
}
