using System.Text.Json.Nodes;
using Insolvia.Core.Access;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Cases;
using Insolvia.Core.Firms;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Insolvia.Api.Tests.Support;

/// <summary>
/// The case-route fixture (test_cases.py's <c>firms</c> + <c>client</c>, and its
/// siblings'): two firms, the cast that decides every read, the API's own
/// memory case stores — the debtor store the real one again, not the merge
/// fake <see cref="AuthHost"/> composes for the firm-side tests.
/// </summary>
internal sealed class CaseWorld : IDisposable
{
    // Firm A: every combination that decides a read.
    public const string Alice = Ids.Alice;   // admin — sees every case
    public const string Dana = Ids.Dana;     // access_all_cases, not admin
    public const string Bob = Ids.Bob;       // paralegal, linked-only
    public const string Greg = "00000000-0000-4000-8000-000000009e69"; // staff — role defaults
    public const string Erin = "00000000-0000-4000-8000-00000000e21e"; // disabled

    // Firm B's admin — the strongest caller the other tenant has.
    public const string Carol = "00000000-0000-4000-8000-0000000ca201";

    // Signed up, never provisioned.
    public const string Frank = "00000000-0000-4000-8000-00000000f4a2";

    public const string Tampa = """ "chapter": 7, "court": "flmb", "division": "tampa" """;

    public CaseWorld(Action<IServiceCollection>? services = null)
    {
        World = new FirmWorld(services: collection =>
        {
            collection.RemoveAll<IDebtorClientLinks>();
            collection.AddSingleton<IDebtorClientLinks>(provider => provider.GetRequiredService<MemoryDebtorStore>());
            services?.Invoke(collection);
        });
    }

    public FirmWorld World { get; }

    public MemoryFirmStore Firms => World.Firms;

    public MemoryAccessLog AccessLog => World.Host.AccessLog;

    public IReadOnlyList<AccessEvent> Events => AccessLog.Events;

    public ICaseStore Cases => Service<ICaseStore>();

    public T Service<T>()
        where T : notnull => World.Host.Services.GetRequiredService<T>();

    /// <summary>The cast of test_cases.py.</summary>
    public async Task<CaseWorld> SeedAsync()
    {
        await World.WithFirmsAsync();
        await World.WithAsync(
            FirmWorld.Member(Alice, isAdmin: true),
            FirmWorld.Member(Dana, accessAllCases: true),
            FirmWorld.Member(Bob, role: FirmRole.Paralegal),
            FirmWorld.Member(Greg, role: FirmRole.Staff),
            FirmWorld.Member(Erin, status: FirmUserStatus.Disabled),
            FirmWorld.Member(Carol, Ids.FirmB, isAdmin: true));
        return this;
    }

    public Task<Answer> Get(string path, string? subject = Alice) => World.Get(path, subject);

    public Task<Answer> Delete(string path, string? subject = Alice) => World.Delete(path, subject);

    public Task<Answer> Post(string path, string? json, string? subject = Alice) => World.Post(path, json, subject);

    public Task<Answer> Put(string path, string? json, string? subject = Alice) => World.Put(path, json, subject);

    public Task<Answer> Patch(string path, string? json, string? subject = Alice) => World.Patch(path, json, subject);

    /// <summary>opening.py's <c>add_client</c>: a client in the caller's firm, its id.</summary>
    public async Task<string> AddClientAsync(string subject = Alice, string? extra = null)
    {
        var body = extra ?? """ "name": {"given": "Jordan", "surname": "Example"} """;
        var answer = await World.Post("/v1/firm/clients", "{" + body + "}", subject);
        Assert.True(answer.Status == 201, answer.Body);
        return answer.Str("id")!;
    }

    /// <summary>test_cases.py's <c>open_case</c>: a case opened for a fresh client.</summary>
    public async Task<JsonObject> OpenCaseAsync(string subject = Alice, int chapter = 7, string court = "flmb", string division = "tampa")
    {
        var clientId = await AddClientAsync(subject);
        var answer = await World.Post("/v1/cases", $$"""{"chapter": {{chapter}}, "court": "{{court}}", "division": "{{division}}", "client_ids": ["{{clientId}}"]}""", subject);
        Assert.True(answer.Status == 201, answer.Body);
        return answer.Object;
    }

    public async Task<string> OpenCaseIdAsync(string subject = Alice, int chapter = 7) =>
        (await OpenCaseAsync(subject, chapter)).Str("id")!;

    /// <summary>A case moved to filed through the PATCH (the docket facts a filing needs).</summary>
    public async Task FileAsync(string caseId, string filedAt = "2026-03-02", string subject = Alice)
    {
        var answer = await Patch($"/v1/cases/{caseId}", $$"""{"status": "filed", "filed_at": "{{filedAt}}", "case_number": "8:26-bk-01234"}""", subject);
        Assert.True(answer.Status == 200, answer.Body);
    }

    /// <summary>The accessor the API would resolve for <paramref name="subject"/>.</summary>
    public async Task<Accessor> AccessorAsync(string subject)
    {
        var user = (await Firms.FindUserAsync(subject))!;
        return new Accessor((await Firms.GetFirmAsync(user.FirmId))!, user);
    }

    /// <summary>A stored case, past the access rule (the Python tests' <c>store.cases[id]</c>).</summary>
    public Task<Case?> StoredAsync(string caseId) => Cases.ReadForWorkerAsync(caseId);

    /// <summary>Firm A's whole working list, as its admin sees it.</summary>
    public async Task<IReadOnlyList<Case>> FirmCasesAsync() =>
        (await Cases.ListForAccessorAsync(await AccessorAsync(Alice), CaseRules.MaxListLimit, null)).Cases;

    /// <summary>Overwrite a stored member in place.</summary>
    public Task ReplaceAsync(FirmUser user) => World.ReplaceAsync(user);

    public void Dispose() => World.Dispose();
}

/// <summary>JSON-object conveniences for the case tests.</summary>
internal static class JsonObjectExtensions
{
    public static string? Str(this JsonObject body, string key) => body[key]?.GetValue<string>();
}
