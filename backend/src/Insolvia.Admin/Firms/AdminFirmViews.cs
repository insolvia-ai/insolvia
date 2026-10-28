using System.Text.Json.Nodes;
using Insolvia.Admin.Http;
using Insolvia.Core;
using Insolvia.Core.Fields;
using Insolvia.Core.Firms;

namespace Insolvia.Admin.Firms;

/// <summary>
/// The two JSON representations this service answers with — insolvia_core's
/// <c>firm_json</c> and <c>firm_user_json</c>. Key order is
/// <see cref="FlaskJson"/>'s to sort.
/// </summary>
/// <remarks>
/// <para><c>firm_json</c> is <c>firm_summary_json</c> PLUS the provisioning
/// provenance, as EXPLICIT NULLS rather than absent keys: <c>createdBy: null</c>
/// tells the portal "nobody recorded" (a seeded firm), where a missing key
/// would read as an older API. The tenant API never answers with it — staff
/// identity is operational metadata for this caller only.</para>
/// <para>Copied from Insolvia.Api's FirmViews (host→host references are
/// forbidden); both belong in Core beside the records, as Python had them.</para>
/// </remarks>
public static class AdminFirmViews
{
    /// <summary><c>firm_json</c>: the firm, its defaults (explicit nulls) and its provenance (explicit nulls).</summary>
    /// <param name="firm">The firm.</param>
    /// <returns>The body.</returns>
    public static JsonObject Firm(Firm firm)
    {
        ArgumentNullException.ThrowIfNull(firm);
        return new JsonObject
        {
            ["id"] = firm.Id,
            ["name"] = firm.Name,
            ["status"] = firm.Status.ToWire(),
            ["createdAt"] = FlaskJson.Millis(firm.CreatedAt),
            ["updatedAt"] = FlaskJson.Millis(firm.UpdatedAt),
            ["createdBy"] = FlaskJson.Text(firm.CreatedBy),
            ["createdByEmail"] = FlaskJson.Text(firm.CreatedByEmail),
            ["defaultCourt"] = FlaskJson.Text(firm.DefaultCourt?.Court),
            ["defaultDivision"] = FlaskJson.Text(firm.DefaultCourt?.Division),
            ["defaultChapter"] = firm.DefaultChapter is { } chapter ? JsonValue.Create(chapter) : null,
            ["letterhead"] = firm.Letterhead is { } letterhead
                ? FlaskJson.Prune(new JsonObject
                {
                    ["name"] = FlaskJson.Text(letterhead.Name),
                    ["address"] = AddressOf(letterhead.Address),
                    ["phone"] = FlaskJson.Text(letterhead.Phone),
                    ["email"] = FlaskJson.Text(letterhead.Email),
                })
                : null,
        };
    }

    /// <summary><c>firm_json</c> with <c>userCount</c> — the list and the single read.</summary>
    /// <param name="firm">The firm.</param>
    /// <param name="userCount">How many users it has.</param>
    /// <returns>The body.</returns>
    public static JsonObject FirmWithCount(Firm firm, int userCount)
    {
        var body = Firm(firm);
        body["userCount"] = userCount;
        return body;
    }

    /// <summary>
    /// <c>firm_user_json</c>: the whole record — this caller administers firms,
    /// which is exactly the reader that representation admits. <c>permissions</c>
    /// is the STORED map, not the effective one.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The body.</returns>
    public static JsonObject User(FirmUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var permissions = new JsonObject();
        foreach (var (feature, level) in user.Permissions)
        {
            permissions[feature.ToWire()] = level.ToWire();
        }

        return new JsonObject
        {
            ["subject"] = user.Subject,
            ["email"] = user.Email,
            ["firstName"] = user.FirstName,
            ["lastName"] = user.LastName,
            ["displayName"] = user.FullName,
            ["role"] = user.Role.ToWire(),
            ["isAdmin"] = user.IsAdmin,
            ["accessAllCases"] = user.AccessAllCases,
            ["permissions"] = permissions,
            ["status"] = user.Status.ToWire(),
            ["createdAt"] = FlaskJson.Millis(user.CreatedAt),
            ["updatedAt"] = FlaskJson.Millis(user.UpdatedAt),
            ["signatureBlock"] = user.SignatureBlock is { } block
                ? FlaskJson.Prune(new JsonObject
                {
                    ["bar_number"] = FlaskJson.Text(block.BarNumber),
                    ["bar_state"] = FlaskJson.Text(block.BarState),
                    ["firm_name"] = FlaskJson.Text(block.FirmName),
                    ["address"] = AddressOf(block.Address),
                    ["phone"] = FlaskJson.Text(block.Phone),
                    ["email"] = FlaskJson.Text(block.Email),
                })
                : null,
        };
    }

    private static JsonObject AddressOf(Address address) => new()
    {
        ["line1"] = FlaskJson.Text(address.Line1),
        ["line2"] = FlaskJson.Text(address.Line2),
        ["city"] = FlaskJson.Text(address.City),
        ["state"] = FlaskJson.Text(address.State),
        ["postal_code"] = FlaskJson.Text(address.PostalCode),
        ["county"] = FlaskJson.Text(address.County),
        ["raw"] = FlaskJson.Text(address.Raw),
    };
}
