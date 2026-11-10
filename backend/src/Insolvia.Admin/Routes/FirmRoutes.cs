using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Admin.Auth;
using Insolvia.Admin.Firms;
using Insolvia.Admin.Http;
using Insolvia.Domain;
using Insolvia.Domain.AdminAudit;
using Insolvia.Domain.Firms;
using Insolvia.Domain.Ports;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Insolvia.Admin.Routes;

/// <summary>
/// Cross-tenant firm administration (#212) — the provisioning surface #178
/// asked for instead of a shell script.
/// </summary>
/// <remarks>
/// <para><b>Firm ids appear in these URLs</b>, which ADR 0009 forbade the
/// tenant API; ADR 0011 records why the rule does not transfer: a staff
/// principal is cross-tenant by definition, authorization is "is this a
/// verified Workspace identity" (settled by <see cref="StaffAuth.RequireStaff{TBuilder}"/>
/// before any handler runs), and the id is the OBJECT of the operation rather
/// than a scope. Every read that takes one still resolves firm-scoped.</para>
/// <para><b>Every mutation audits</b>, after it succeeds: the audit table
/// records what HAPPENED, the firm row is the record of what exists. A failed
/// mutation throws before reaching the audit call.</para>
/// <para>Every handler takes a <see cref="CancellationToken"/> beside the
/// <see cref="HttpContext"/>: a handler shaped exactly <c>HttpContext → Task</c>
/// binds as a raw <c>RequestDelegate</c>, which silently skips endpoint filters.</para>
/// </remarks>
public static class FirmRoutes
{
    /// <summary>Maps the six staff routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder.</returns>
    public static IEndpointRouteBuilder MapFirmRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/v1/firms", ProvisionAsync).RequireStaff().RequestBody<ProvisionRequest>();
        app.MapGet("/v1/firms", ListAsync).RequireStaff();
        app.MapGet("/v1/firms/{firmId}", GetAsync).RequireStaff();
        app.MapPatch("/v1/firms/{firmId}", SetStatusAsync).RequireStaff().RequestBody<FirmStatusRequest>();
        app.MapGet("/v1/firms/{firmId}/users", ListUsersAsync).RequireStaff();
        app.MapPost("/v1/firms/{firmId}/users/{subject}/resend-invite", ResendInviteAsync).RequireStaff();
        return app;
    }

    /// <summary>Provision a firm and invite its first administrator.</summary>
    /// <remarks>
    /// The first user is an administrator with every permission, whatever the
    /// body says: a firm whose first user cannot administer it is bricked at
    /// birth. A 409 means a firm or user with that identity already exists.
    /// </remarks>
    // ORDER: pool account first, then rows, then audit — a duplicate address
    // dies as a 409 before any row exists; a row failure strands only a pool
    // account. The audit row comes after the firm exists because the table's
    // foreign key needs it.
    internal static async Task<Created<ProvisionedFirm>> ProvisionAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var payload = await BodyAsync(context, cancellationToken).ConfigureAwait(false);
        var firmDraft = FirmParser.ParseFirmCreation(payload);

        if (payload["admin"] is not JsonObject admin)
        {
            throw new FieldValidationException(new Dictionary<string, string>
            {
                ["admin"] = "A firm needs its first administrator: {email, firstName, lastName}.",
            });
        }

        // All three name keys are forwarded and the PARSER decides which wins;
        // deciding here would put that precedence in two places.
        var forwarded = new JsonObject { ["email"] = admin["email"]?.DeepClone() };
        foreach (var key in new[] { "firstName", "lastName", "displayName" })
        {
            if (admin.ContainsKey(key))
            {
                forwarded[key] = admin[key]?.DeepClone();
            }
        }

        forwarded["role"] = admin.ContainsKey("role") ? admin["role"]?.DeepClone() : "attorney";
        forwarded["isAdmin"] = true;
        var adminDraft = FirmParser.ParseFirmUserCreation(forwarded);

        var staff = context.CurrentStaff();
        var clock = context.Clock();
        var subject = await context.Directory().CreateUserAsync(adminDraft.Email, cancellationToken).ConfigureAwait(false);

        var firm = FirmLifecycle.CreateFirm(firmDraft, createdBy: staff.Subject, createdByEmail: staff.Email, clock: clock);
        var store = context.FirmStore();
        await store.CreateFirmAsync(firm, cancellationToken).ConfigureAwait(false);

        var adminUser = FirmLifecycle.CreateFirmUser(adminDraft, firm.Id, subject, clock);
        await store.AddUserAsync(adminUser, cancellationToken).ConfigureAwait(false);

        await context.Audit().RecordAsync(
            AdminEvents.Record(firm.Id, AdminAction.FirmProvision, staff.Subject, staff.Email, $"{firm.Name} · first administrator {adminUser.Email}", clock),
            cancellationToken).ConfigureAwait(false);

        return TypedResults.Created((string?)null, new ProvisionedFirm(FirmView.Of(firm), FirmUserView.Of(adminUser)));
    }

    /// <summary>Every firm, with its user count.</summary>
    // One user listing per firm: honest about its price at tens-of-firms scale.
    internal static async Task<FirmList> ListAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var store = context.FirmStore();
        var firms = new List<FirmSummaryView>();
        foreach (var firm in await store.ListFirmsAsync(cancellationToken).ConfigureAwait(false))
        {
            var users = await store.ListUsersAsync(firm.Id, cancellationToken).ConfigureAwait(false);
            firms.Add(FirmSummaryView.Of(firm, users.Count));
        }

        return new FirmList(firms);
    }

    /// <summary>One firm, with its user count.</summary>
    internal static async Task<FirmSummaryView> GetAsync(HttpContext context, string firmId, CancellationToken cancellationToken)
    {
        var store = context.FirmStore();
        var firm = await store.GetFirmAsync(firmId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("no such firm");
        var users = await store.ListUsersAsync(firm.Id, cancellationToken).ConfigureAwait(false);
        return FirmSummaryView.Of(firm, users.Count);
    }

    /// <summary>Suspend or reactivate a firm.</summary>
    /// <remarks>
    /// A suspended firm stays in place, but none of its users can reach the API
    /// until it is reactivated — immediately, since the API re-reads the firm on
    /// every request. <c>status</c> is the only field this route writes: the
    /// firm's name belongs to its own admins.
    /// </remarks>
    internal static async Task<FirmView> SetStatusAsync(HttpContext context, string firmId, CancellationToken cancellationToken)
    {
        var payload = await BodyAsync(context, cancellationToken).ConfigureAwait(false);
        if (payload["status"] is not JsonValue statusNode
            || statusNode.GetValueKind() != JsonValueKind.String
            || !Wire.TryParse<FirmStatus>(statusNode.GetValue<string>(), out var status))
        {
            throw new FieldValidationException(new Dictionary<string, string>
            {
                ["status"] = "Status must be one of " + string.Join(", ", Wire.Names<FirmStatus>()) + ".",
            });
        }

        var store = context.FirmStore();
        var firm = await store.GetFirmAsync(firmId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("no such firm");

        // Null: deleted between the read and the write — the same 404 an
        // unknown id gets, and the store's condition stopped a resurrection.
        var clock = context.Clock();
        var updated = await store.UpdateFirmAsync(FirmLifecycle.SetFirmStatus(firm, status, clock), cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("no such firm");

        var staff = context.CurrentStaff();
        await context.Audit().RecordAsync(
            AdminEvents.Record(firm.Id, status == FirmStatus.Suspended ? AdminAction.FirmSuspend : AdminAction.FirmReactivate, staff.Subject, staff.Email, clock: clock),
            cancellationToken).ConfigureAwait(false);

        return FirmView.Of(updated);
    }

    /// <summary>A firm's users.</summary>
    internal static async Task<FirmUserList> ListUsersAsync(HttpContext context, string firmId, CancellationToken cancellationToken)
    {
        var store = context.FirmStore();
        if (await store.GetFirmAsync(firmId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new NotFoundException("no such firm");
        }

        var users = await store.ListUsersAsync(firmId, cancellationToken).ConfigureAwait(false);
        return new FirmUserList([.. users.Select(FirmUserView.Of)]);
    }

    /// <summary>Send a user their sign-in invitation again, with a fresh temporary password.</summary>
    /// <remarks>
    /// A 409 for a user who has already signed in: forgot-password is their way
    /// back in. A subject from another firm is the same 404 as an unknown one.
    /// </remarks>
    internal static async Task<NoContent> ResendInviteAsync(HttpContext context, string firmId, string subject, CancellationToken cancellationToken)
    {
        var store = context.FirmStore();
        if (await store.GetFirmAsync(firmId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new NotFoundException("no such firm");
        }

        var user = await store.GetUserAsync(firmId, subject, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("no such user in that firm");

        await context.Directory().ResendInviteAsync(user.Email, cancellationToken).ConfigureAwait(false);

        var staff = context.CurrentStaff();
        await context.Audit().RecordAsync(
            AdminEvents.Record(firmId, AdminAction.InviteResend, staff.Subject, staff.Email, user.Email, context.Clock()),
            cancellationToken).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    // A missing or non-JSON content type, a parse failure and a non-object
    // are all the same field error, never a 415.
    private static async Task<JsonObject> BodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        JsonNode? parsed = null;
        if (IsJson(request.ContentType))
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            parsed = RequestBodies.Parse(text);
        }

        return parsed as JsonObject ?? throw new FieldValidationException(new Dictionary<string, string>
        {
            ["body"] = "A JSON object body is required.",
        });
    }

    // application/json, or application/*+json.
    private static bool IsJson(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        var mime = contentType.Split(';', 2)[0].Trim();
        return mime.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || (mime.StartsWith("application/", StringComparison.OrdinalIgnoreCase) && mime.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
    }

    private static IFirmStore FirmStore(this HttpContext context) =>
        context.RequestServices.GetService<IFirmStore>() ?? throw new InvalidOperationException("firm store is not composed");

    private static IUserDirectory Directory(this HttpContext context) =>
        context.RequestServices.GetService<IUserDirectory>() ?? throw new InvalidOperationException("user directory is not composed");

    private static IAdminAuditLog Audit(this HttpContext context) =>
        context.RequestServices.GetService<IAdminAuditLog>() ?? throw new InvalidOperationException("admin audit log is not composed");

    private static TimeProvider Clock(this HttpContext context) =>
        context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
}
