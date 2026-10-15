# RentalManagement-1

**A role-based rental application and property management system** built with **ASP.NET Core MVC (.NET 10), EF Core 10, SQL Server, and ASP.NET Core Identity** for the Troy Web Consulting .NET technical assessment.

Applicants can find rental units, complete and submit an application, follow its progress, and update a returned application. Property managers maintain the property inventory and process submitted applications using a **claim-based review queue**. Approvals produce a **12-month lease**, with safeguards against overlapping leases.

> **For assessment reviewers:** Start with [Run the project in 5 minutes](#run-the-project-in-5-minutes), then follow the [suggested demo walkthrough](#suggested-demo-walkthrough). The sections below explain the architecture, business rules, API, concurrency protections, tests, and environment configuration.

## At a glance

| Item | Details |
|---|---|
| Solution | `RentalManagement-1.slnx` |
| Projects | `RentalManagement.Web`, `RentalManagement.Core`, `RentalManagement.Tests` — all directly beneath the solution root |
| Platform | C# / .NET 10 / ASP.NET Core MVC / Razor |
| Data | SQL Server, Entity Framework Core 10, code-first migrations |
| Identity | ASP.NET Core Identity, Applicant and PropertyManager roles |
| UI | Bootstrap 5, partial views, view components, JavaScript, accessible responsive layout |
| Testing | xUnit, EF Core InMemory and SQLite, optional SQL Server integration tests |
| API documentation | OpenAPI JSON and Swagger UI in Development |
| Default local URL | <https://localhost:7064> |

**Important access rule:** The landing page is public; viewing rental units, managing applications, and calling the application JSON endpoints all require sign-in. Applicants see only their own applications; managers can review the broader application set.

## Contents

1. [Run the project in 5 minutes](#run-the-project-in-5-minutes)
2. [Sample logins and demo data](#sample-logins-and-demo-data)
3. [Suggested demo walkthrough](#suggested-demo-walkthrough)
4. [Features and workflows](#features-and-workflows)
5. [Architecture and project structure](#architecture-and-project-structure)
6. [Setup on a new machine](#setup-on-a-new-machine)
7. [Database configuration and initialization](#database-configuration-and-initialization)
8. [Application grid and JSON API](#application-grid-and-json-api)
9. [Review queue and concurrency](#review-queue-and-concurrency)
10. [Security and validation](#security-and-validation)
11. [Running and debugging tests](#running-and-debugging-tests)
12. [Visual Studio debugging guide](#visual-studio-debugging-guide)
13. [Configuration reference](#configuration-reference)
14. [Technical trade-offs and known limitations](#technical-trade-offs-and-known-limitations)
15. [Troubleshooting](#troubleshooting)
16. [Repository preparation](#repository-preparation)

---

## Run the project in 5 minutes

**Prerequisites:** Windows with **.NET 10 SDK**, **SQL Server LocalDB** (or another SQL Server instance), and optionally **Visual Studio 2026** with *ASP.NET and web development*. The initial NuGet restore needs internet access.

From a PowerShell terminal in the directory containing `RentalManagement-1.slnx`:

```powershell
# Verify that .NET 10 is installed
dotnet --list-sdks

# If using LocalDB, confirm/start the default instance
sqllocaldb info
sqllocaldb start MSSQLLocalDB

# Trust the local development certificate (first time only)
dotnet dev-certs https --trust

# Restore, compile and run tests
dotnet restore RentalManagement-1.slnx
dotnet build RentalManagement-1.slnx
dotnet test RentalManagement-1.slnx

# Start the web application with the Development HTTPS profile
dotnet run --project RentalManagement.Web --launch-profile https
```

Navigate to **<https://localhost:7064>**. On its first run, the app **creates `RentalManagementDb`, applies committed EF Core migrations, and seeds sample data**. No SQL script or manual `dotnet ef database update` is required.

**Visual Studio alternative:** Open `RentalManagement-1.slnx`, set `RentalManagement.Web` as the startup project, choose the `https` launch profile and press **F5**.

> If your SQL Server is not `(localdb)\MSSQLLocalDB`, configure the connection string before running. See [Database configuration](#database-configuration-and-initialization).

## Sample logins and demo data

Demo credentials are available **only when development seeding is enabled**. These are deliberately nonproduction accounts.

| Role | Username | Password |
|---|---|---|
| Property manager | `manager1@rental.test` | `Passw0rd!` |
| Property manager | `manager2@rental.test` | `Passw0rd!` |
| Applicant | `applicant1@rental.test` | `Passw0rd!` |
| Applicant | `applicant2@rental.test` | `Passw0rd!` |

There are **eight seeded applicants** (`applicant1` through `applicant8`), **two seeded managers**, **five properties**, **26 units**, and **five unit types**. The seeded dataset includes **14 applications** spanning all seven statuses (two per status), plus application history and active/expired lease examples. One unit type is inactive but remains associated with an existing unit to demonstrate preservation of historical references.

## Suggested demo walkthrough

This sequence highlights both the core assessment requirements and the additional functionality.

1. **Public landing page:** Open the site while signed out. Notice the project introduction and sign-up/login entry points. Rental-unit inventory is not public.
2. **Applicant experience:** Sign in as `applicant1@rental.test`. Open available units, try the property/bedroom/rent/type filters, and start an application for an eligible unit.
3. **Application form:** Enter applicant details and residence history. Use **Save & continue**, **Back**, and **Submit** to demonstrate section validation, saving, and state transitions.
4. **Application list:** Open **My applications**; change the status/property filters, sort a column, select a different page size, and navigate pages. The grid fetches JSON and performs paging, filtering, and sorting **in SQL Server**.
5. **Manager review queue:** Sign out and sign in as `manager1@rental.test`. Open **Review queue**, claim a Submitted application, review its details, and release it to make it available again.
6. **Two-manager protection:** In a separate private browser session, sign in as `manager2@rental.test`. Attempt to claim an application already held by manager1; the second claim is refused. Release it as manager1, then claim it as manager2.
7. **Review outcome:** As the claim owner, approve (choose a lease start date), return with feedback, or deny with feedback. Open the **History** to see the change trail. Use another sample application if a status is no longer eligible.
8. **API and code:** Open `/swagger` while signed in, or `/openapi/v1.json`. Debug `ApplicationsApiController.Grid`, `ApplicationService.ListPageAsync`, and `ApplicationReviewService.ClaimAsync` to show the API, SQL paging, and concurrency flow.
9. **Tests:** Run the xUnit suite from **Test Explorer** or `dotnet test RentalManagement-1.slnx`.

## Features and workflows

### Applicant

- Register or log in as an Applicant.
- Search available rental units with database-side filters for property, unit type, bedrooms, and maximum rent.
- Complete a multi-step, single-page application: **Applicant information → Residence history → Summary**.
- Manage residences in modal dialogs and save validated sections.
- Submit a completed application; track statuses and review comments.
- View **My applications** in a reusable, paged, sortable, filterable grid.
- Withdraw eligible applications; correct and resubmit applications returned by a manager.

### Property manager

- Create, edit, and remove properties and units using modal forms.
- Use the review queue to **Claim**, **Review**, or **Release** applications.
- View applications with server-side sorting, filtering, and paging.
- Approve with a 12-month lease start date, return an application with a comment, or deny it with a comment.
- See the application's history, including claims, releases, comments, and review outcomes.
- Prevent conflicting claims and active/overlapping unit leases through backend business rules and concurrency controls.

### Application lifecycle

```text
Draft ── Submit ──> Submitted ── Claim ──> Under Review
                        ^                   |  |  |  |
                        |       Release ────┘  |  |  |
                        |                      |  |  └── Deny ──> Denied
Returned ── Resubmit ──┘          Return <────┘  └──── Approve ──> Approved + 12-month lease

Eligible applicants can also withdraw their own application.
Withdrawn, Denied, and Approved are terminal states.
```

The state-machine and domain services validate transitions on the server. The UI does not determine whether an operation is allowed.

## Architecture and project structure

```text
RentalManagement-1/
├── RentalManagement-1.slnx
├── RentalManagement.Web/
│   ├── Controllers/              MVC pages, actions, and JSON endpoints
│   ├── ViewModels/               Web-facing request/response models
│   ├── ViewComponents/           Reusable independent page regions
│   ├── Views/                    Razor pages and partial views
│   ├── Infrastructure/           MVC helpers and OpenAPI configuration
│   ├── wwwroot/                  CSS, JavaScript, Bootstrap/jQuery
│   ├── Properties/launchSettings.json
│   └── Program.cs               DI, middleware, authentication, initialization
├── RentalManagement.Core/
│   ├── Domain/                   Entities, statuses, transitions, lease rules
│   ├── Services/                 Business logic and database queries
│   ├── Data/                     DbContext, entity configuration, migrations
│   │   ├── Migrations/
│   │   └── Seeding/              Repeatable development fixtures
│   └── DependencyInjection.cs
├── RentalManagement.Tests/
│   ├── Domain/
│   ├── Services/
│   ├── Data/
│   └── Web/
├── Directory.Build.props
├── global.json
├── nuget.config
└── dotnet-tools.json
```

### Separation of responsibilities

| Layer | Responsibility |
|---|---|
| **Web / Presentation** | HTTP endpoints, Razor UI, form binding, authorization attributes, antiforgery protection, modal responses, JSON serialization |
| **Core / Domain** | Application state machine, unit and lease constraints, claim ownership, entities and history |
| **Core / Services** | Orchestrates business operations, validates rules, manages queries, returns typed results |
| **Core / Data** | SQL Server EF Core persistence, model mappings, migrations, development seeding |
| **Tests** | Domain behavior, application workflows, authorization, query translation, concurrency, migration scenarios |

Business rules remain in Core rather than in JavaScript or controllers. Controllers process requests and delegate to services. Shared view components (including `ApplicationGrid`) allow focused updates without reloading the whole page. Modal validation failures return an HTML partial with HTTP **422**; successes return JSON and the relevant page components refresh.

## Setup on a new machine

1. **Install** .NET 10 SDK; optionally Visual Studio 2026 with the ASP.NET/web workload. Install SQL Server LocalDB or provide a reachable SQL Server 2019+ instance.
2. **Clone or extract** the repository and open a terminal **at the solution root**, the folder containing `RentalManagement-1.slnx`.
3. **Verify** `dotnet --list-sdks` includes version `10.0.x`. If using LocalDB, run `sqllocaldb info` and `sqllocaldb start MSSQLLocalDB`.
4. **Configure your database** only if the default LocalDB connection is unsuitable. See below for safe user-secrets examples.
5. **Trust HTTPS** with `dotnet dev-certs https --trust`.
6. **Restore / build / test** using the commands in [Quick start](#run-the-project-in-5-minutes).
7. **Run** with `dotnet run --project RentalManagement.Web --launch-profile https` or Visual Studio **F5**.
8. **Verify** the terminal reports `Database ready`, browse to the local URL, and sign in using a demo account.

No npm install or separate front-end bundling command is required: local static assets are included in `wwwroot`.

## Database configuration and initialization

The application reads `ConnectionStrings:DefaultConnection`. In Development, `RentalManagement.Web/appsettings.Development.json` defaults to:

```text
Server=(localdb)\MSSQLLocalDB;Database=RentalManagementDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True
```

**Using a different Windows SQL Server instance:** From the solution root, store the override in **ASP.NET Core user secrets**, not a tracked JSON file:

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=.\SQLEXPRESS;Database=RentalManagementDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True" --project RentalManagement.Web

dotnet user-secrets list --project RentalManagement.Web
```

Replace `.\SQLEXPRESS` with your server name. In Visual Studio, right-click `RentalManagement.Web` and choose **Manage User Secrets**. For SQL authentication, store credentials in user secrets and use `User ID=...;Password=...` instead of `Trusted_Connection=True`.

**Other environments:** Set `ConnectionStrings__DefaultConnection` as an environment variable or use your deployment's secret store. Production configuration deliberately supplies no hard-coded database connection and does **not** enable demo seeding by default.

### Startup sequence

1. `Program.cs` configures SQL Server EF Core, Identity, authorization, Core services, MVC, and OpenAPI.
2. `InitializeDatabaseAsync` connects to SQL Server and executes `Database.MigrateAsync`.
3. In Development, `Seed:Enabled=true` loads deterministic sample roles, users, properties, units, applications, statuses, history, and leases.
4. Seeding checks for existing records so **restarting the application does not duplicate demo data**.
5. After initialization, the HTTP server begins accepting requests.

The checked-in migrations are located in `RentalManagement.Core/Data/Migrations`. To make a **new** model migration during development:

```powershell
dotnet tool restore
dotnet ef migrations add MeaningfulMigrationName --project RentalManagement.Core --startup-project RentalManagement.Web --output-dir Data/Migrations
```

On the next startup, the migration is applied automatically. **Resetting data is destructive**: only drop `RentalManagementDb` if you intentionally want to discard local data and recreate the sample dataset.

## Application grid and JSON API

`ApplicationGrid` is a reusable Razor view component backed by `wwwroot/js/application-grid.js`. Its rows are loaded from a **JSON endpoint**; SQL Server handles filtered total counts, sorting, and pagination before results are serialized.

### Application list endpoint

```http
GET /api/applications/grid?page=1&pageSize=10&sortBy=submittedDate&sortDirection=desc
```

| Parameter | Accepted values | Default |
|---|---|---|
| `page` | Positive integer (1-based) | `1` |
| `pageSize` | `10`, `25`, `50`, `100` | `10` |
| `sortBy` | `id`, `applicantName`, `propertyName`, `unitNumber`, `status`, `submittedDate`, `updatedDate` | `submittedDate` |
| `sortDirection` | `asc`, `desc` | Date columns descending; others ascending |
| `status` | Valid application status | All statuses |
| `propertyId` | Property ID | All accessible properties |

Example response shape (illustrative, not a promise of identical seeded IDs):

```json
{
  "page": 1,
  "pageSize": 10,
  "totalCount": 2,
  "totalPages": 1,
  "sortBy": "submittedDate",
  "sortDirection": "desc",
  "status": null,
  "propertyId": null,
  "items": [
    {
      "id": 4,
      "applicantName": "Example Applicant",
      "propertyName": "Example Property",
      "unitNumber": "103",
      "status": "Submitted",
      "statusLabel": "Submitted",
      "submittedDate": "2026-10-02T17:57:00+00:00",
      "updatedDate": "2026-10-02T17:57:00+00:00",
      "url": "/Applications/Edit/4",
      "actionLabel": "Review"
    }
  ]
}
```

Access and error behavior:

- Authentication uses the application sign-in cookie. **Unauthenticated:** `401`. **Insufficient role:** `403`.
- **Applicants** only receive records belonging to the signed-in applicant; **managers** can see all applications.
- Invalid query parameters produce `400` with `application/problem+json` details.
- Pages beyond the end return an empty `items` array while preserving the actual `totalCount`.
- Sort columns are **allowlisted**, and the query includes a stable ID tie-breaker to avoid duplicate/missing rows between pages.
- Grid filters, sort, and page selection can be represented in the URL for bookmarking.

**Development API docs:**

- **OpenAPI JSON:** <https://localhost:7064/openapi/v1.json> (available without login in Development).
- **Swagger UI:** <https://localhost:7064/swagger> (sign-in required). The UI can test the endpoints with the session cookie.

These development documentation routes are not exposed in Production.

## Review queue and concurrency

The review queue is a deliberate extension beyond basic approve/deny functionality. A submitted application **must be claimed** before a manager can issue a review decision.

| Queue | Status / ownership | Default ordering | Actions |
|---|---|---|---|
| **Available for review** | Submitted and unclaimed | Oldest submission first | Claim, Open |
| **My claimed applications** | Under Review, claimed by the signed-in manager | Most recently claimed first | Review, Release, Open |

### Claim lifecycle

1. Manager A selects **Claim** on a Submitted application.
2. The status changes to `UnderReview`; the server records the authenticated manager's ID and claim timestamp.
3. Manager B cannot claim or review A's application. The operation returns a conflict and does not overwrite A's claim.
4. Manager A can **Release** the application; it returns to the Submitted queue, retaining its original submission date.
5. Alternatively, A can **Approve**, **Return**, or **Deny** it; a review outcome clears the claim and records a status-history entry.

The database includes a **GUID concurrency stamp** on `RentalApplication`. Claiming changes the stamp; EF Core's update uses the originally loaded stamp as a concurrency condition. If two managers claim simultaneously, only one update succeeds. A database check constraint ensures claim owner/timestamp are populated only for `UnderReview`, and required for that status. This protects the workflow across independent web requests and application instances, not merely within one page session.

Approval also checks for active and overlapping leases; unit-level concurrency protection guards competing approvals. A lease runs from its selected start date through **12 months minus one day**.

**Relevant endpoints:**

| Route | Purpose |
|---|---|
| `GET /Applications/ReviewQueue` | Manager review queue page |
| `GET /api/applications/review-queue?queue=available` | Available queue JSON |
| `GET /api/applications/review-queue?queue=myClaims` | Current manager's claimed queue JSON |
| `POST /Applications/Claim/{id}` | Claim a submitted application |
| `GET /Applications/Release/{id}` / `POST /Applications/Release/{id}` | Confirm and release an owned claim |
| `GET /Applications/Review/{id}` / `POST /Applications/Review/{id}` | Review an application claimed by the current manager |

Queue JSON supports page size, page, sorting, property filters, and `claimedDate` sorting. Mutating form requests require antiforgery tokens. Claim/release updates refresh the affected grids and surface conflicts without a full-page reload.

## Security and validation

- **ASP.NET Core Identity** authenticates users and provides role-aware authorization.
- MVC and API routes require authentication by default; public landing and account actions explicitly allow anonymous access.
- Core services independently verify ownership and role; hiding buttons alone is **not** the security boundary.
- A user cannot assign a different claim owner by posting a user ID; ownership comes from the authenticated principal.
- Anti-forgery validation applies to submitted MVC forms and modal POST actions.
- Server-side validation rejects invalid application sections, transitions, review outcomes, and unsafe list parameters.
- Other users' private application records return **404** rather than revealing record existence.
- Response headers include `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, and `Referrer-Policy`.
- Demo credentials and seed data are for **Development only**, not deployment.

## Running and debugging tests

Run everything:

```powershell
dotnet test RentalManagement-1.slnx
```

Or use **Visual Studio → Test → Test Explorer → Run All**. The default suite uses EF Core InMemory, SQLite in-memory relational tests, and SQL-generation checks that do not require a separate SQL Server test database. **Optional SQL Server-specific integration tests are skipped without an opt-in connection string.**

| Coverage | Examples |
|---|---|
| Domain | State transitions, terminal states, lease terms, inactive unit types |
| Applicant | Save/submit, validation, residence history, withdrawal, correction and resubmission |
| Property manager | CRUD, review outcomes, mandatory comments, status history |
| Concurrency | Simultaneous claims, simultaneous reviews, competing unit approvals |
| Data queries | Real translated SQLite paging/sorting/filtering queries, counts, stable ordering, scoped visibility |
| Web/API | Authorization, controller responses, malformed query handling, role-sensitive JSON |
| Database | Idempotent seeding and optional SQL Server migration/check-constraint integration scenarios |

To opt into SQL Server tests, provide a test server where the user can **create and drop temporary databases**:

```powershell
$env:RENTALMANAGEMENT_TEST_SQLSERVER = 'Server=.\SQLEXPRESS;Trusted_Connection=True;TrustServerCertificate=True'
dotnet test RentalManagement-1.slnx --filter 'FullyQualifiedName~ReviewQueueSqlServerTests'
dotnet test RentalManagement-1.slnx --filter 'FullyQualifiedName~ApplicationGridQueryTests'
```

**Caution:** These tests intentionally create and delete their own uniquely named test databases. Use an isolated development SQL Server, never a production connection.

## Visual Studio debugging guide

Open the solution and run `RentalManagement.Web` with the **Debug / https** launch profile. Both Web and Core have debugger symbols. Useful breakpoint locations:

| Scenario | File / method |
|---|---|
| Initial migration and seeding | `Core/Data/DatabaseInitializer.cs` → `InitializeDatabaseAsync` |
| Page submission | `Web/Controllers/ApplicationsController.cs` → `Edit` (POST) |
| Application rules and database queries | `Core/Services/Applications/ApplicationService.cs` |
| Grid request | `Web/Controllers/ApplicationsApiController.cs` → `Grid` |
| Paging/sorting query | `Core/Services/Applications/ApplicationService.cs` → `ListPageAsync` |
| Query parameter allowlist | `Core/Services/Applications/ApplicationListQuery.cs` → `Create` |
| Review queue lookup | `Core/Services/Applications/ReviewQueueService.cs` → `ListPageAsync` |
| Claim conflict handling | `Core/Services/Applications/ApplicationReviewService.cs` → `ClaimAsync` / `SaveAsync` |
| State machine | `Core/Domain/RentalApplication.cs` and `ApplicationStateMachine.cs` |
| Lease creation and overlap validation | `Core/Services/Applications/ApplicationReviewService.cs` → `ReviewAsync` |

For SQL inspection, set `Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command` to `Information` in a **local** development settings override. The grid should issue SQL count and page queries with `ORDER BY` and `OFFSET/FETCH` rather than loading all rows into memory.

## Configuration reference

| Key / file | Meaning |
|---|---|
| `global.json` | Requires a compatible .NET 10 SDK feature band |
| `Directory.Build.props` | Shared `net10.0`, nullable, C#, build/debug defaults |
| `nuget.config` | NuGet.org package source |
| `dotnet-tools.json` | Local `dotnet-ef` CLI version |
| `appsettings.Development.json` | Default LocalDB connection and development-only seed options |
| `ConnectionStrings:DefaultConnection` | Database connection setting |
| `ConnectionStrings__DefaultConnection` | Equivalent environment-variable override |
| `Seed:Enabled` | Enables or disables sample data seeding |
| `Seed:DefaultPassword` | Seeded demonstration-account password |
| `ASPNETCORE_ENVIRONMENT` | `Development` enables seed configuration, developer tooling, and OpenAPI/Swagger routes |
| `RENTALMANAGEMENT_TEST_SQLSERVER` | Opt-in connection for disposable SQL Server test databases |
| `Properties/launchSettings.json` | Local HTTPS `7064` and HTTP `5111` profiles |

## Technical trade-offs and known limitations

This is a **technical assessment application**, not a production-ready property-management SaaS product.

- **Self-registration permits choosing PropertyManager.** Production should provision/approve manager accounts through a controlled administrative workflow.
- **Email confirmation and password reset are not implemented.** Identity has a per-account failed-login lockout, but production security would need a complete account recovery and operational strategy.
- **Claims do not expire automatically.** An abandoned claim remains with that manager until released/reviewed; production would likely need supervised reassignment and audit controls.
- **Concurrent edits in multiple tabs by the same applicant** can overwrite prior edits; consider optimistic concurrency on the editing workflow if expanding this project.
- **The Privacy page contains template placeholder text** and should be completed before a real deployment.
- **Initial schema migration runs on startup** for reproducible local assessment setup. A production deployment could move migrations into a controlled deployment step.

These items are explicitly identified rather than represented as completed features.

## Troubleshooting

| Problem | What to check |
|---|---|
| `dotnet` cannot find SDK | Install .NET 10; confirm `dotnet --list-sdks` |
| NuGet restore errors | Confirm internet access and retry `dotnet restore RentalManagement-1.slnx` |
| Cannot connect to `(localdb)\MSSQLLocalDB` | Install SQL Server LocalDB; run `sqllocaldb info` then `sqllocaldb start MSSQLLocalDB` |
| Connection string is not configured | Use the `Development` launch profile or set `ConnectionStrings__DefaultConnection` / user secrets |
| SQL permission error during startup | Ensure the account can create `RentalManagementDb` or has appropriate permissions on an existing database |
| Certificate/browser HTTPS warning | `dotnet dev-certs https --trust` (if necessary, clean/retrust local development certificates) |
| Port 7064 or 5111 already in use | Stop the other local server or update the launch profile |
| Browser returns login page for units/API | Sign in first; only the landing and account pages are anonymous |
| API returns 401/403 | Check your browser's authentication cookie and the required role |
| API returns 400 | Inspect `application/problem+json` for invalid filter, page or sort parameters |
| SQL Server integration tests are skipped | This is expected until `RENTALMANAGEMENT_TEST_SQLSERVER` is set |
| Restore from command-line MSBuild fails | Run `dotnet restore RentalManagement-1.slnx` before building |

## Repository preparation

The root `.gitignore` excludes common generated output (`bin/`, `obj/`, `.vs/`). Before publishing to GitHub or sharing the assessment:

```powershell
git status --short --untracked-files=all
```

Confirm that **no machine-specific settings, local secrets, IDE cache, compiled binaries, or database files** are included. Keep the three source projects at solution root. The original ZIP used for local review also contains `.git`, `.vs`, and build directories; those are **not necessary for a clean source-only submission**.

If a repository URL has not been supplied yet, create the GitHub repository and upload the reviewed source rather than leaving a placeholder clone URL in the documentation.

---

**Assessment focus:** The project demonstrates full-stack .NET delivery, clean separation of concerns, SQL-backed querying, explicit business-state rules, authentication and authorization, multi-user concurrency handling, test coverage, and developer-friendly setup and diagnostics.
