using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.Clients;
using Insolvia.Core.Debtors;
using Insolvia.Core.Documents;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;
using Insolvia.Core.TaxIds;

namespace Insolvia.Migrator.Seeding;

/// <summary>
/// Converges an environment on <c>seeds/&lt;env&gt;.json</c> and the fixture
/// versions it names — services/admin entrypoints/seed.py's <c>load</c>,
/// ported line for line in intent. seeds/README.md and ADR 0021 own the why;
/// the rules that are load-bearing:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Rows go through Core.</b> Every fixture body is parsed by the
/// parser the matching route uses, so a malformed fixture fails with the API's
/// own field errors and a new required field breaks seeding loudly.</item>
/// <item><b>It converges.</b> Firms are keyed on the people in them; every
/// other row's id is derived (<see cref="SeedIds"/>), so a second run finds
/// what the first wrote and writes nothing. Rows that exist are LEFT ALONE,
/// even if the fixture has changed: a seeded environment is somewhere people
/// work, and a changed fixture is a new version.</item>
/// <item><b>A refusal writes nothing for what it refuses.</b> Accounts resolve
/// before the firm row; every reference and every debtor body is resolved and
/// parsed before the first case write.</item>
/// <item><b>Under <c>check</c> nothing is written</b> — no row and no account —
/// and every missing thing is NAMED in the write path's vocabulary.</item>
/// <item><b>Lifecycle (ADR 0027).</b> A fixture case is created by
/// <see cref="CaseRules.Create"/> at <c>intake</c>, exactly as
/// <c>POST /v1/cases</c> creates one; no committed fixture case is filed, so
/// no lifecycle event is written and no new fixture version was needed.</item>
/// </list>
/// </remarks>
public sealed class SeedLoader
{
    private const string Ref = "$ref";
    private const string Handle = "$handle";
    private const string DebtorsCollection = "debtors";
    private const string ClientsCollection = "clients";
    private static readonly FilingRole[] ClientRoles = [FilingRole.Debtor1, FilingRole.Debtor2];

    private readonly SeedStores _stores;
    private readonly ISeedAccounts _accounts;
    private readonly SeedTarget _target;
    private readonly TextWriter _out;
    private readonly TimeProvider? _clock;

    /// <summary>A loader writing through <paramref name="stores"/> into <paramref name="target"/>.</summary>
    /// <param name="stores">The stores (and the fixture bytes).</param>
    /// <param name="accounts">The identity provider's half.</param>
    /// <param name="target">The environment; production never gets here (<see cref="SeedTarget.For"/>).</param>
    /// <param name="output">Where the report goes, one line per thing seen.</param>
    /// <param name="clock">The clock rows are stamped with.</param>
    public SeedLoader(SeedStores stores, ISeedAccounts accounts, SeedTarget target, TextWriter output, TimeProvider? clock = null)
    {
        _stores = stores ?? throw new ArgumentNullException(nameof(stores));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _out = output ?? throw new ArgumentNullException(nameof(output));
        _clock = clock;
    }

    /// <summary>
    /// Load <paramref name="environmentFixture"/>. Returns how many things
    /// were missing (written now, or — under <paramref name="check"/> — still
    /// missing). Throws <see cref="SeedRefusedException"/> for a guard or a
    /// fixture refusal, and the API's own validation exceptions for a body
    /// its parser refuses.
    /// </summary>
    /// <param name="environmentFixture">The <c>seeds/&lt;env&gt;.json</c> path.</param>
    /// <param name="environment">Reads one variable, for <c>${VAR}</c>.</param>
    /// <param name="check">Report only; write nothing.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The number of missing things.</returns>
    public async Task<int> LoadAsync(string environmentFixture, Func<string, string?> environment, bool check, CancellationToken cancellationToken = default)
    {
        var fixture = SeedFixtures.Load(environmentFixture, environment);
        var firms = SeedJson.List(fixture["firms"]);
        var missing = await SeedFirmsAsync(firms, check, cancellationToken).ConfigureAwait(false);
        var entries = SeedJson.List(fixture["cases"]);
        if (entries.Count > 0)
        {
            missing += await SeedCasesAsync(entries, firms, environmentFixture, check, cancellationToken).ConfigureAwait(false);
        }

        return missing;
    }

    // ── firms ──────────────────────────────────────────────────────────

    /// <summary>
    /// The pool subject for one fixture person. A <c>password</c> says "this
    /// environment owns its accounts" and the account is converged; without
    /// one it must already exist. Under check nothing is created.
    /// </summary>
    private async Task<string> SubjectForAsync(JsonObject person, bool check, CancellationToken cancellationToken)
    {
        var email = SeedJson.Str(person["email"]);
        var password = SeedJson.Text(person["password"]);
        return string.IsNullOrEmpty(password) || check
            ? await _accounts.SubjectOfAsync(email, cancellationToken).ConfigureAwait(false)
            : await _accounts.EnsureAsync(email, password, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Converge each fixture firm, per USER rather than per firm: a firm has no
    /// natural key (its id is minted), so "is this firm here?" is answered
    /// through its people — and a fixture that grows a colleague adds them to
    /// the existing firm instead of creating a second one beside it.
    /// </summary>
    private async Task<int> SeedFirmsAsync(IReadOnlyList<JsonNode?> entries, bool check, CancellationToken cancellationToken)
    {
        var missing = 0;
        foreach (var node in entries)
        {
            var entry = node as JsonObject ?? throw new SeedRefusedException("each firm in the fixture must be an object");
            var name = SeedJson.Str(entry["name"]);
            var users = SeedJson.List(entry["users"]);
            if (users.Count == 0)
            {
                throw new SeedRefusedException($"firm '{name}' has no users — a firm nobody can sign in to is the derelict state ADR 0009 refuses");
            }

            // Accounts first, and all of them, before a single row: a fixture
            // naming somebody who cannot be provisioned fails while the
            // database is still untouched, not half a firm in.
            var drafts = new List<(FirmUserDraft Draft, string Subject)>();
            foreach (var user in users)
            {
                var person = SeedJson.Body(user);
                var draft = FirmParser.ParseFirmUserCreation(person);
                drafts.Add((draft, await SubjectForAsync(person, check, cancellationToken).ConfigureAwait(false)));
            }

            var placed = new Dictionary<string, FirmUser>(StringComparer.Ordinal);
            foreach (var (_, subject) in drafts)
            {
                if (await _stores.Firms.FindUserAsync(subject, cancellationToken).ConfigureAwait(false) is { } found)
                {
                    placed[subject] = found;
                }
            }

            var firmIds = placed.Values.Select(user => user.FirmId).Distinct(StringComparer.Ordinal).ToList();
            if (firmIds.Count > 1)
            {
                throw new SeedRefusedException($"the people in '{name}' are already split across {firmIds.Count} firms; refusing to guess which one is meant");
            }

            var absent = drafts.Where(pair => !placed.ContainsKey(pair.Subject)).ToList();
            if (absent.Count == 0)
            {
                await _out.WriteLineAsync($"firm '{name}': already seeded").ConfigureAwait(false);
                continue;
            }

            missing += absent.Count;
            if (check)
            {
                await _out.WriteLineAsync($"firm '{name}': {absent.Count} user(s) missing").ConfigureAwait(false);
                continue;
            }

            string firmId;
            if (firmIds.Count == 1)
            {
                firmId = firmIds[0];
                await _out.WriteLineAsync($"firm '{name}': adding {absent.Count} user(s)").ConfigureAwait(false);
            }
            else
            {
                // No provenance: nobody provisioned a fixture, and the admin
                // portal's createdBy: null says exactly that.
                var firm = FirmLifecycle.CreateFirm(FirmParser.ParseFirmCreation(new JsonObject { ["name"] = entry["name"]?.DeepClone() }), clock: _clock);
                await _stores.Firms.CreateFirmAsync(firm, cancellationToken).ConfigureAwait(false);
                firmId = firm.Id;
                await _out.WriteLineAsync($"created firm {firm.Name} ({firm.Id})").ConfigureAwait(false);
            }

            foreach (var (draft, subject) in absent)
            {
                await _stores.Firms.AddUserAsync(FirmLifecycle.CreateFirmUser(draft, firmId, subject, _clock), cancellationToken).ConfigureAwait(false);
                await _out.WriteLineAsync($"  added {draft.Email} ({draft.Role.ToWire()}, admin={(draft.IsAdmin ? "True" : "False")})").ConfigureAwait(false);
            }
        }

        return missing;
    }

    // ── cases ──────────────────────────────────────────────────────────

    private async Task<int> SeedCasesAsync(IReadOnlyList<JsonNode?> entries, IReadOnlyList<JsonNode?> firms, string environmentFixture, bool check, CancellationToken cancellationToken)
    {
        var missing = 0;
        var versions = new Dictionary<string, (JsonObject Cases, JsonObject Manifest)>(StringComparer.Ordinal);
        foreach (var node in entries)
        {
            var entry = node as JsonObject ?? throw new SeedRefusedException("each case in the fixture must be an object");
            var version = SeedJson.Str(entry["fixture"]);
            if (!versions.TryGetValue(version, out var loaded))
            {
                loaded = SeedFixtures.Version(environmentFixture, version);
                versions[version] = loaded;
            }

            var wanted = SeedJson.Str(entry["case"]);
            var spec = SeedJson.List(loaded.Cases["cases"]).OfType<JsonObject>().FirstOrDefault(candidate => SeedJson.Text(candidate["handle"]) == wanted)
                ?? throw new SeedRefusedException($"fixture {version} has no case with handle '{wanted}'");

            var resolved = await FirmAndSubjectAsync(entry, firms, check, cancellationToken).ConfigureAwait(false);
            if (resolved is not { } placed)
            {
                await _out.WriteLineAsync($"  case '{wanted}': its firm is not seeded yet").ConfigureAwait(false);
                missing++;
                continue;
            }

            missing += await SeedOneCaseAsync(spec, loaded.Manifest, version, VersionClients(wanted, loaded.Cases), placed.FirmId, placed.Subject, check, cancellationToken).ConfigureAwait(false);

            var clients = SeedJson.List(entry["clients"]);
            if (clients.Count == 0)
            {
                continue;
            }

            var caseId = SeedIds.Derive(_target.CaseTarget, placed.FirmId, version, SeedJson.Str(spec["handle"]));
            if (await _stores.Cases.ReadForWorkerAsync(caseId, cancellationToken).ConfigureAwait(false) is null)
            {
                // Only reachable under check on an unseeded case: a binding
                // must never point at a case that is not there.
                await _out.WriteLineAsync($"  case '{wanted}': clients wait for the case").ConfigureAwait(false);
                missing += clients.Count;
                continue;
            }

            missing += await SeedClientsAsync(clients, caseId, placed.FirmId, placed.Subject, check, cancellationToken).ConfigureAwait(false);
        }

        return missing;
    }

    /// <summary>
    /// The firm a fixture case lands in and the person who opened it — both
    /// resolved through the SEEDED rows, so a case can only belong to a firm
    /// this fixture describes. Null under check when the person is not seeded
    /// yet: on a fresh environment the firms are missing too.
    /// </summary>
    private async Task<(string FirmId, string Subject)?> FirmAndSubjectAsync(JsonObject entry, IReadOnlyList<JsonNode?> firms, bool check, CancellationToken cancellationToken)
    {
        var firmName = SeedJson.Str(entry["firm"]);
        var handle = SeedJson.Str(entry["openedBy"]);
        foreach (var firm in firms.OfType<JsonObject>())
        {
            if (SeedJson.Text(firm["name"]) != firmName)
            {
                continue;
            }

            foreach (var user in SeedJson.List(firm["users"]).OfType<JsonObject>())
            {
                if (SeedJson.Text(user["handle"]) != handle)
                {
                    continue;
                }

                string subject;
                try
                {
                    subject = await _accounts.SubjectOfAsync(SeedJson.Str(user["email"]), cancellationToken).ConfigureAwait(false);
                }
                catch (SeedRefusedException) when (check)
                {
                    return null;
                }

                var placed = await _stores.Firms.FindUserAsync(subject, cancellationToken).ConfigureAwait(false);
                if (placed is null && check)
                {
                    return null;
                }

                return placed is null
                    ? throw new SeedRefusedException($"'{handle}' is not yet in the firm table — firms seed before cases, so this is a missing account")
                    : (placed.FirmId, subject);
            }

            throw new SeedRefusedException($"firm '{firmName}' has nobody with handle '{handle}'");
        }

        throw new SeedRefusedException($"case fixture names firm '{firmName}', which this fixture lacks");
    }

    /// <summary>The version's top-level <c>clients</c> (ADR 0022), by handle — refused on a missing or duplicate handle.</summary>
    private static Dictionary<string, JsonObject> VersionClients(string handle, JsonObject cases)
    {
        var found = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in SeedJson.List(cases[ClientsCollection]))
        {
            var clientHandle = node is JsonObject obj ? SeedJson.Text(obj["handle"]) : null;
            if (string.IsNullOrEmpty(clientHandle))
            {
                throw new SeedRefusedException($"case '{handle}': every fixture client needs a handle");
            }

            if (!found.TryAdd(clientHandle, (JsonObject)node!))
            {
                throw new SeedRefusedException($"case '{handle}': two fixture clients have handle '{clientHandle}'");
            }
        }

        return found;
    }

    /// <summary>Converge one fixture case. Returns how many things were missing, each one named.</summary>
    private async Task<int> SeedOneCaseAsync(
        JsonObject spec,
        JsonObject manifest,
        string version,
        Dictionary<string, JsonObject> clients,
        string firmId,
        string createdBy,
        bool check,
        CancellationToken cancellationToken)
    {
        var handle = SeedJson.Str(spec["handle"]);
        if (handle.Length == 0)
        {
            throw new SeedRefusedException("every fixture case needs a handle");
        }

        var caseId = SeedIds.Derive(_target.CaseTarget, firmId, version, handle);
        var missing = 0;

        var debtors = spec["debtors"] is null ? [] : spec["debtors"] as JsonObject
            ?? throw new SeedRefusedException($"case '{handle}': debtors must be an object keyed by role");
        var collections = spec["collections"] is null ? [] : spec["collections"] as JsonObject
            ?? throw new SeedRefusedException($"case '{handle}': collections must be an object");

        // What a $ref resolves to in THIS target: a debtor already here keeps
        // the id it has; one about to be written gets the id it will have.
        var roles = new List<(string Name, FilingRole Role)>();
        var debtorIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, _) in debtors)
        {
            if (!Wire.TryParse<FilingRole>(name, out var role))
            {
                throw new SeedRefusedException($"case '{handle}': '{name}' is not a filing role");
            }

            roles.Add((name, role));
            var found = await _stores.Debtors.GetAsync(caseId, role, cancellationToken).ConfigureAwait(false);
            debtorIds[name] = found?.Id ?? SeedIds.Derive(caseId, "debtor", name);
        }

        var handles = CollectionHandles(handle, collections);
        var clientIds = clients.Keys.ToDictionary(
            clientHandle => clientHandle,
            clientHandle => SeedIds.Derive(_target.FirmTarget, firmId, version, ClientsCollection, clientHandle),
            StringComparer.Ordinal);
        JsonNode? Resolve(JsonNode? body) => Resolved(body, handle, caseId, handles, debtorIds, clientIds);

        // Every reference resolved ONCE before anything is written, so a
        // fixture naming a record it does not contain is refused untouched.
        var resolvedDebtors = (JsonObject)Resolve(debtors)!;
        _ = Resolve(collections);

        // WHICH CLIENT EACH DEBTOR IS (ADR 0022): Debtor 1 and Debtor 2 must
        // name one, because a case is opened for a client. Pre-client
        // versions (v1–v3) are refused here by design.
        var linked = new Dictionary<FilingRole, string>();
        foreach (var (name, role) in roles)
        {
            var clientId = resolvedDebtors[name] is JsonObject body ? body["client_id"] : null;
            if (clientId is null)
            {
                if (ClientRoles.Contains(role))
                {
                    throw new SeedRefusedException(
                        $"case '{handle}': {name} names no client — every Debtor 1 and Debtor 2 carries \"client_id\": {{\"$ref\": \"clients/<handle>\"}}");
                }

                continue;
            }

            linked[role] = SeedJson.Text(clientId) ?? throw new SeedRefusedException($"case '{handle}': {name}'s client_id is not a $ref");
        }

        if (linked.Values.Distinct(StringComparer.Ordinal).Count() != linked.Count)
        {
            throw new SeedRefusedException($"case '{handle}': one client names two debtors — one client, one role");
        }

        // The clients before the case that names them, parsed by the route's
        // own parser and converged like every other seeded row.
        foreach (var (name, clientId) in clientIds.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!linked.ContainsValue(clientId))
            {
                continue;
            }

            var body = SeedJson.Body(clients[name]);
            body.Remove("handle");
            var draft = FirmClientRules.Parse(body, clock: _clock);
            if (await _stores.Firms.GetClientAsync(firmId, clientId, cancellationToken).ConfigureAwait(false) is not null)
            {
                continue;
            }

            missing++;
            if (check)
            {
                await _out.WriteLineAsync($"    client '{name}': missing").ConfigureAwait(false);
                continue;
            }

            await _stores.Firms.CreateClientAsync(FirmClientRules.Create(draft, firmId, createdBy, _clock) with { Id = clientId }, cancellationToken).ConfigureAwait(false);
            await _out.WriteLineAsync($"    client '{name}': created").ConfigureAwait(false);
        }

        // Every debtor body parsed before the first case write.
        foreach (var (name, _) in roles)
        {
            DebtorRules.Parse(SeedJson.Body(resolvedDebtors[name]));
        }

        // A debtor's record, drafted — and its tax id SEALED, as the API seals
        // one: fresh ref, the firm in the context, the digits nowhere but the
        // envelope — only when it is about to be written.
        async Task<Debtor> DebtorRecordAsync(string name, FilingRole role, DateTimeOffset caseCreatedAt, ITaxIdStore? sealInto = null)
        {
            var draft = DebtorRules.Parse(SeedJson.Body(resolvedDebtors[name]));
            var taxId = await TaxIdRules.StoreAsync(draft.TaxId, null, firmId, caseId, _stores.TaxIdCipher, sealInto ?? _stores.TaxIds, _clock, cancellationToken).ConfigureAwait(false);
            var clientId = linked.GetValueOrDefault(role);
            return DebtorRules.Create(draft, caseId, role, taxId, clientId, clientId is null ? null : caseCreatedAt, _clock) with { Id = debtorIds[name] };
        }

        // The case: parsed by the route's own parser, stamped by the same
        // factory, given its derived id, a matching assignment AND its client
        // debtors, in the one transaction POST /v1/cases uses.
        var written = new HashSet<FilingRole>();
        var stored = await _stores.Cases.ReadForWorkerAsync(caseId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            missing++;
            if (check)
            {
                await _out.WriteLineAsync($"  case '{handle}': missing").ConfigureAwait(false);
            }
            else
            {
                var draft = CaseRules.ParseCreation(new JsonObject
                {
                    ["chapter"] = spec["chapter"]?.DeepClone(),
                    ["court"] = spec["court"]?.DeepClone(),
                    ["division"] = spec["division"]?.DeepClone(),
                    ["client_ids"] = new JsonArray([.. ClientRoles.Where(linked.ContainsKey).Select(role => (JsonNode)linked[role])]),
                });
                var (minted, _) = CaseRules.Create(draft, firmId, createdBy, _clock);
                var created = minted with { Id = caseId };
                // The opening debtors' tax ids are sealed now and STORED once
                // the case row exists: a sealed tax id belongs to its case
                // (the schema's foreign key), and the case is born with them.
                var sealedLater = new DeferredTaxIdStore(_stores.TaxIds);
                var opening = new List<Debtor>();
                foreach (var role in ClientRoles)
                {
                    if (linked.ContainsKey(role) && await _stores.Debtors.GetAsync(caseId, role, cancellationToken).ConfigureAwait(false) is null)
                    {
                        opening.Add(await DebtorRecordAsync(role.ToWire(), role, created.CreatedAt, sealedLater).ConfigureAwait(false));
                    }
                }

                await _stores.Cases.CreateAsync(created, CaseRules.Assign(created, createdBy, createdBy, _clock), opening, cancellationToken).ConfigureAwait(false);
                await sealedLater.FlushAsync(cancellationToken).ConfigureAwait(false);
                written.UnionWith(opening.Select(debtor => debtor.FilingRole));
                stored = created;
                await _out.WriteLineAsync($"  case '{handle}': created ({caseId})").ConfigureAwait(false);
                foreach (var role in written.Select(role => role.ToWire()).Order(StringComparer.Ordinal))
                {
                    await _out.WriteLineAsync($"    debtor {role}: created").ConfigureAwait(false);
                }
            }
        }
        else
        {
            await _out.WriteLineAsync($"  case '{handle}': present").ConfigureAwait(false);
        }

        // The rest of the debtors, keyed by filing role — the API's natural key.
        foreach (var (name, role) in roles)
        {
            if (written.Contains(role) || await _stores.Debtors.GetAsync(caseId, role, cancellationToken).ConfigureAwait(false) is not null)
            {
                continue;
            }

            missing++;
            if (check || stored is null)
            {
                await _out.WriteLineAsync($"    debtor {name}: missing").ConfigureAwait(false);
                continue;
            }

            await _stores.Debtors.CreateAsync(await DebtorRecordAsync(name, role, stored.CreatedAt).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            await _out.WriteLineAsync($"    debtor {name}: created").ConfigureAwait(false);
        }

        // Collection items in fixture order, each id derived from its
        // position, so a re-run finds it and a $ref resolves before it exists.
        foreach (var (name, items) in collections)
        {
            var kind = CaseCollections.Find(name) ?? throw new SeedRefusedException($"case '{handle}': unknown collection '{name}'");
            var index = 0;
            foreach (var item in SeedJson.List(items))
            {
                var draft = kind.ParseDraft(SeedJson.Body(Resolve(item)));
                var entityId = SeedIds.Derive(caseId, name, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (await _stores.Entities.GetAsync(caseId, kind, entityId, cancellationToken).ConfigureAwait(false) is null)
                {
                    missing++;
                    if (check)
                    {
                        await _out.WriteLineAsync($"    {name}[{index}]: missing").ConfigureAwait(false);
                    }
                    else
                    {
                        var now = Clock.Now(_clock);
                        await _stores.Entities.CreateAsync(kind.Restore(entityId, caseId, now, now, draft), cancellationToken).ConfigureAwait(false);
                        await _out.WriteLineAsync($"    {name}[{index}]: created").ConfigureAwait(false);
                    }
                }

                index++;
            }
        }

        missing += await SeedDocumentsAsync(spec, manifest, version, handle, caseId, createdBy, check, cancellationToken).ConfigureAwait(false);
        return missing;
    }

    /// <summary>
    /// Documents: the row AND the bytes. The bytes go first, server-side, and
    /// the row is written <c>stored</c> only once a stat has seen them at the
    /// manifest's size — the same order and confirmation the complete route uses.
    /// </summary>
    private async Task<int> SeedDocumentsAsync(JsonObject spec, JsonObject manifest, string version, string handle, string caseId, string createdBy, bool check, CancellationToken cancellationToken)
    {
        var missing = 0;
        foreach (var document in SeedJson.List(spec["documents"]).OfType<JsonObject>())
        {
            var source = SeedJson.Str(document["object"]);
            var listed = (manifest["objects"] as JsonObject)?[source] as JsonObject
                ?? throw new SeedRefusedException($"case '{handle}': document object '{source}' is not in manifest.json");
            var fileName = SeedJson.Str(document["fileName"]);
            var documentId = SeedIds.Derive(caseId, "document", source);
            var existing = await _stores.Documents.GetAsync(caseId, documentId, cancellationToken).ConfigureAwait(false);
            if (existing is { Status: DocumentStatus.Stored })
            {
                continue;
            }

            missing++;
            if (check)
            {
                // A row that is not stored is an interrupted copy, not an
                // unseeded case — fixed differently, so said differently.
                await _out.WriteLineAsync($"    document {fileName}: {(existing is null ? "missing" : "not stored")}").ConfigureAwait(false);
                continue;
            }

            var size = listed["size"]?.DeepClone();
            var draft = DocumentRules.ParseUpload(new JsonObject
            {
                ["kind"] = document["kind"]?.DeepClone(),
                ["fileName"] = document["fileName"]?.DeepClone(),
                ["contentType"] = document["contentType"]?.DeepClone(),
                ["byteSize"] = size,
            });
            var storageRef = DocumentRules.ObjectKey(caseId, documentId);
            await _stores.Objects.CopyAsync($"{version}/{source}", storageRef, draft.ContentType.ToWire(), cancellationToken).ConfigureAwait(false);
            var blob = await _stores.Blobs.StatAsync(storageRef, cancellationToken).ConfigureAwait(false)
                ?? throw new SeedRefusedException($"case '{handle}': the copy of {source} did not land");
            if (blob.ByteSize != draft.ByteSize)
            {
                throw new SeedRefusedException($"case '{handle}': {source} landed at {blob.ByteSize} bytes, manifest says {draft.ByteSize}");
            }

            var confirmed = DocumentRules.Confirm(
                DocumentRules.Create(draft, caseId, createdBy, clock: _clock) with { Id = documentId, StorageRef = storageRef },
                blob);
            if (existing is null)
            {
                await _stores.Documents.CreateAsync(confirmed, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _stores.Documents.UpdateAsync(confirmed, cancellationToken).ConfigureAwait(false);
            }

            await _out.WriteLineAsync($"    document {fileName}: stored").ConfigureAwait(false);
        }

        return missing;
    }

    /// <summary>
    /// Converge a fixture case's PORTAL clients (ADR 0023) — through the same
    /// parser, planner and store the invitation route uses, except that no
    /// invitation is mailed: the password is set here. A binding that exists
    /// is left alone, revoked or not.
    /// </summary>
    private async Task<int> SeedClientsAsync(IReadOnlyList<JsonNode?> entries, string caseId, string firmId, string invitedBy, bool check, CancellationToken cancellationToken)
    {
        var missing = 0;
        foreach (var node in entries)
        {
            var entry = node as JsonObject ?? throw new SeedRefusedException("each client in a fixture case must be an object");
            var handle = SeedJson.Str(entry["handle"]);
            var invitation = new JsonObject();
            foreach (var key in new[] { "email", "displayName", "roles" })
            {
                if (entry.ContainsKey(key))
                {
                    invitation[key] = entry[key]?.DeepClone();
                }
            }

            var draft = ClientBindings.ParseInvitation(invitation);
            string subject;
            try
            {
                subject = await SubjectForAsync(entry, check, cancellationToken).ConfigureAwait(false);
            }
            catch (SeedRefusedException) when (check)
            {
                await _out.WriteLineAsync($"    client '{handle}': no account").ConfigureAwait(false);
                missing++;
                continue;
            }

            if (await _stores.Firms.FindUserAsync(subject, cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new SeedRefusedException($"client '{handle}' is also a firm user — a subject is staff or a client, never both (ADR 0023)");
            }

            if (await _stores.Bindings.GetAsync(firmId, subject, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                await _out.WriteLineAsync($"    client '{handle}': present ({existing.Status.ToWire()})").ConfigureAwait(false);
                continue;
            }

            missing++;
            if (check)
            {
                await _out.WriteLineAsync($"    client '{handle}': missing").ConfigureAwait(false);
                continue;
            }

            var binding = ClientBindings.CreateBinding(draft, firmId, caseId, subject, invitedBy, _clock);
            try
            {
                var narrowed = ClientBindings.PlanBinding(binding, null, await _stores.Bindings.ListForCaseAsync(caseId, cancellationToken).ConfigureAwait(false));
                await _stores.Bindings.BindAsync(binding, narrowed, cancellationToken).ConfigureAwait(false);
            }
            catch (ConflictException conflict)
            {
                // The API's 409 is this loader's refusal: a fixture giving two
                // clients one debtor role is a fixture to fix, not to half-load.
                throw new SeedRefusedException($"client '{handle}': {conflict.Message}", conflict);
            }

            await _out.WriteLineAsync($"    client '{handle}': bound ({string.Join(", ", binding.Roles.Select(role => role.ToWire()))})").ConfigureAwait(false);
        }

        return missing;
    }

    // ── references between a case's own records ───────────────────────
    //
    // A case is a graph: a claim names its creditor, an income summary its
    // debtor. A fixture publishes NO ids, so it writes a reference instead —
    //   {"$handle": "car-loan", ...}    on the collection item referred TO
    //   {"$ref": "claims/car-loan"}     wherever an id of it is wanted
    //   {"$ref": "debtors/debtor_1"}    a debtor, by filing role
    //   {"$ref": "clients/<handle>"}    one of the version's firm clients
    // — and the loader swaps each $ref for the id that record has (or will
    // have) in THIS target before the route's own parser sees the body.

    private static Dictionary<string, Dictionary<string, int>> CollectionHandles(string handle, JsonObject collections)
    {
        var found = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var (name, items) in collections)
        {
            var index = 0;
            foreach (var body in SeedJson.List(items))
            {
                if (body is JsonObject obj && obj.ContainsKey(Handle) && obj[Handle] is not null)
                {
                    var itemHandle = SeedJson.Str(obj[Handle]);
                    var seen = found.TryGetValue(name, out var existing) ? existing : found[name] = new Dictionary<string, int>(StringComparer.Ordinal);
                    if (!seen.TryAdd(itemHandle, index))
                    {
                        throw new SeedRefusedException($"case '{handle}': {name} has two items with $handle '{itemHandle}'");
                    }
                }

                index++;
            }
        }

        return found;
    }

    private static JsonNode? Resolved(
        JsonNode? node,
        string handle,
        string caseId,
        Dictionary<string, Dictionary<string, int>> handles,
        Dictionary<string, string> debtorIds,
        Dictionary<string, string> clientIds)
    {
        switch (node)
        {
            case JsonArray array:
                return new JsonArray([.. array.Select(item => Resolved(item, handle, caseId, handles, debtorIds, clientIds))]);
            case JsonObject obj when obj.ContainsKey(Ref):
                var target = SeedJson.Text(obj[Ref]);
                if (obj.Count != 1 || target is null || !target.Contains('/', StringComparison.Ordinal))
                {
                    throw new SeedRefusedException($"case '{handle}': a reference is {{\"$ref\": \"<collection>/<handle>\"}} and nothing else, not {obj.ToJsonString()}");
                }

                var slash = target.IndexOf('/', StringComparison.Ordinal);
                var (collection, name) = (target[..slash], target[(slash + 1)..]);
                if (collection == ClientsCollection)
                {
                    return clientIds.TryGetValue(name, out var clientId)
                        ? clientId
                        : throw new SeedRefusedException($"case '{handle}': $ref '{target}' names no fixture client");
                }

                if (collection == DebtorsCollection)
                {
                    return debtorIds.TryGetValue(name, out var debtorId)
                        ? debtorId
                        : throw new SeedRefusedException($"case '{handle}': $ref '{target}' names no fixture debtor");
                }

                return handles.TryGetValue(collection, out var named) && named.TryGetValue(name, out var position)
                    ? SeedIds.Derive(caseId, collection, position.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    : throw new SeedRefusedException($"case '{handle}': $ref '{target}' names no item with that $handle");
            case JsonObject obj:
                return new JsonObject([.. obj
                    .Where(pair => pair.Key != Handle)
                    .Select(pair => KeyValuePair.Create(pair.Key, Resolved(pair.Value, handle, caseId, handles, debtorIds, clientIds)))]);
            default:
                return node?.DeepClone();
        }
    }
}

/// <summary>
/// A tax-id store that reads through and holds its writes until
/// <see cref="FlushAsync"/> — so a debtor can be sealed before the case row
/// its sealed tax id belongs to exists.
/// </summary>
/// <param name="inner">The real store.</param>
internal sealed class DeferredTaxIdStore(ITaxIdStore inner) : ITaxIdStore
{
    private readonly List<(string CaseId, SealedTaxId Sealed)> _pending = [];

    /// <inheritdoc />
    public Task PutAsync(string caseId, SealedTaxId sealedId, CancellationToken cancellationToken = default)
    {
        _pending.Add((caseId, sealedId));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SealedTaxId?> GetAsync(string caseId, string reference, CancellationToken cancellationToken = default) =>
        inner.GetAsync(caseId, reference, cancellationToken);

    /// <summary>Writes every held seal through, in order.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        foreach (var (caseId, sealedId) in _pending)
        {
            await inner.PutAsync(caseId, sealedId, cancellationToken).ConfigureAwait(false);
        }

        _pending.Clear();
    }
}
