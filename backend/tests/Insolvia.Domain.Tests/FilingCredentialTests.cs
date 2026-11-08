using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Insolvia.Core.AccessLog;
using Insolvia.Core.FilingAuthorizations;
using Insolvia.Core.FilingCredentials;
using Insolvia.Core.Memory;

namespace Insolvia.Core.Tests;

/// <summary>
/// The CM/ECF credential vault, translated from test_filing_credentials.py.
/// The seal and open are the KMS envelope's stand-in (<see cref="FakeCipher"/>);
/// the envelope and its key policy are 18.12's.
/// </summary>
public sealed class FilingCredentialTests
{
    private const string Firm = "firm-0001";
    private const string OtherFirm = "firm-0002";
    private const string Attorney = "00000000-0000-4000-8000-00000000a771";
    private const string OtherAttorney = "00000000-0000-4000-8000-00000000b771";
    private const string Filing = "filing-0001";
    private const string Login = "FAKE-ECF-USER";
    private const string Password = "FAKE-ECF-PASSWORD-not-a-real-one";
    private const long SignedInAt = 4_102_444_800;

    private static readonly string Valid32 = new('A', 32);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A fresh base32 TOTP seed, as PACER shows one.</summary>
    private static string FreshSeed()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        return new string([.. RandomNumberGenerator.GetBytes(32).Select(b => alphabet[b % 32])]);
    }

    // ── Parsing ─────────────────────────────────────────────────────

    [Fact]
    public void A_complete_enrolment_parses()
    {
        var seed = FreshSeed();
        var enrolment = FilingCredentialRules.ParseEnrolment(new JsonObject
        {
            ["login"] = Login,
            ["password"] = Password,
            ["totp_seed"] = seed,
            ["courts"] = new JsonArray("txsb", "flmb", "txsb"),
        });
        Assert.Equal(Login, enrolment.Login);
        Assert.Equal(new CredentialSecret(Password, seed), enrolment.Secret);
        Assert.Equal(["flmb", "txsb"], enrolment.Courts);
    }

    [Fact]
    public void A_seed_shown_grouped_and_lowercase_is_normalised()
    {
        var seed = FreshSeed();
        var grouped = string.Join(" ", seed.Chunk(4).Select(chunk => new string(chunk))).ToLowerInvariant();
        Assert.Equal(seed, FilingCredentialRules.ParseEnrolment(new JsonObject { ["login"] = Login, ["password"] = Password, ["totp_seed"] = grouped }).Secret.TotpSeed);
    }

    public static TheoryData<string, string> Problems() => new()
    {
        { $$"""{"password": "{{Password}}", "totp_seed": "{{Valid32}}"}""", "login" },
        { $$"""{"login": "has space", "password": "{{Password}}", "totp_seed": "{{Valid32}}"}""", "login" },
        { $$"""{"login": "{{Login}}", "totp_seed": "{{Valid32}}"}""", "password" },
        { $$"""{"login": "{{Login}}", "password": "{{new string('x', 129)}}", "totp_seed": "{{Valid32}}"}""", "password" },
        { $$"""{"login": "{{Login}}", "password": "tab\there", "totp_seed": "{{Valid32}}"}""", "password" },
        { $$"""{"login": "{{Login}}", "password": "{{Password}}", "totp_seed": "ABC"}""", "totp_seed" },
        { $$"""{"login": "{{Login}}", "password": "{{Password}}", "totp_seed": "{{new string('0', 32)}}"}""", "totp_seed" },
        { $$"""{"login": "{{Login}}", "password": "{{Password}}"}""", "totp_seed" },
        { $$"""{"login": "{{Login}}", "password": "{{Password}}", "totp_seed": "{{Valid32}}", "courts": ["zzzz"]}""", "courts" },
        { $$"""{"login": "{{Login}}", "password": "{{Password}}", "totp_seed": "{{Valid32}}", "courts": "txsb"}""", "courts" },
    };

    [Theory]
    [MemberData(nameof(Problems))]
    public void Each_problem_is_reported_on_its_field(string body, string field) =>
        Assert.Contains(field, Assert.Throws<FieldValidationException>(() => FilingCredentialRules.ParseEnrolment(Body.Of(body))).Fields.Keys);

    [Fact]
    public void A_refusal_never_echoes_what_was_submitted()
    {
        var secretish = "NOT-ECHOED-" + FreshSeed();
        var error = Assert.Throws<FieldValidationException>(() => FilingCredentialRules.ParseEnrolment(new JsonObject { ["login"] = secretish + " x", ["password"] = "", ["totp_seed"] = secretish }));
        Assert.DoesNotContain(error.Fields.Values, message => message.Contains(secretish, StringComparison.Ordinal));
        Assert.DoesNotContain(secretish, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_secret_does_not_print()
    {
        var secret = new CredentialSecret(Password, FreshSeed());
        Assert.DoesNotContain(Password, secret.ToString(), StringComparison.Ordinal);
        var enrolment = FilingCredentialRules.ParseEnrolment(new JsonObject { ["login"] = Login, ["password"] = Password, ["totp_seed"] = secret.TotpSeed });
        Assert.DoesNotContain(Password, enrolment.ToString(), StringComparison.Ordinal);
    }

    // ── Enrolment ───────────────────────────────────────────────────

    [Fact]
    public async Task Enrolment_seals_and_the_stored_item_holds_no_plaintext()
    {
        var vault = await Vault.CreateAsync();
        var seed = FreshSeed();
        var credential = await vault.EnrolAsync(seed);
        var stored = (await vault.Store.GetAsync(Firm, Attorney, credential.CredentialId, Ct))!;
        var flat = stored.ToString();
        Assert.DoesNotContain(Password, flat, StringComparison.Ordinal);
        Assert.DoesNotContain(seed, flat, StringComparison.Ordinal);
        Assert.Equal((Login, CredentialStatus.Active), (stored.Login, stored.Status));
    }

    // test_the_status_view_is_pinned_and_carries_no_secret_material: the JSON view; dropped.

    [Fact]
    public async Task Enrolment_is_logged_under_the_credential()
    {
        var vault = await Vault.CreateAsync();
        var credential = await vault.EnrolAsync(FreshSeed());
        var recorded = Assert.Single(vault.Log.Events);
        Assert.Equal(AccessAction.CredentialEnrol, recorded.Action);
        Assert.Equal((credential.CredentialId, Attorney, null), (recorded.CredentialId, recorded.Principal, recorded.CaseId));
    }

    [Fact]
    public async Task The_same_login_twice_is_a_conflict_whatever_its_case()
    {
        var vault = await Vault.CreateAsync();
        await vault.EnrolAsync(FreshSeed());
        await Assert.ThrowsAsync<ConflictException>(() => vault.EnrolAsync(FreshSeed(), login: Login.ToLowerInvariant()));
    }

    [Fact]
    public async Task Another_attorney_may_enrol_the_same_login_name()
    {
        var vault = await Vault.CreateAsync();
        await vault.EnrolAsync(FreshSeed());
        await vault.EnrolAsync(FreshSeed(), attorney: OtherAttorney);
        Assert.Single(await FilingCredentialRules.ListAsync(Firm, Attorney, vault.Store, Ct));
    }

    // ── Opening ─────────────────────────────────────────────────────

    [Fact]
    public async Task The_worker_opens_what_the_api_sealed()
    {
        var vault = await Vault.CreateAsync();
        var seed = FreshSeed();
        var credential = await vault.EnrolAsync(seed);
        Assert.Equal(new CredentialSecret(Password, seed), await vault.OpenAsync(credential.CredentialId));
    }

    [Fact]
    public async Task Every_open_records_the_attorney_the_filing_and_the_purpose()
    {
        var vault = await Vault.CreateAsync();
        var credential = await vault.EnrolAsync(FreshSeed());
        await vault.OpenAsync(credential.CredentialId);
        var recorded = vault.Log.Events[^1];
        Assert.Equal(AccessAction.CredentialOpen, recorded.Action);
        Assert.Equal((credential.CredentialId, Attorney, Filing, "sign_in"), (recorded.CredentialId, recorded.Principal, recorded.FilingId, recorded.Purpose));
        Assert.Equal($"CREDENTIAL#{credential.CredentialId}", recorded.SubjectKey);
    }

    [Fact]
    public async Task Revoke_destroys_the_item_and_the_next_open_fails_and_is_still_logged()
    {
        var vault = await Vault.CreateAsync();
        var credential = await vault.EnrolAsync(FreshSeed());
        Assert.True(await FilingCredentialRules.RevokeAsync(credential.CredentialId, Firm, Attorney, vault.Store, vault.Log, cancellationToken: Ct));
        Assert.Empty(vault.Store.Items);
        await Assert.ThrowsAsync<CredentialUnavailableException>(() => vault.OpenAsync(credential.CredentialId));
        Assert.Equal([AccessAction.CredentialEnrol, AccessAction.CredentialRevoke, AccessAction.CredentialOpen], vault.Log.Events.Select(e => e.Action));
    }

    [Fact]
    public async Task Another_attorney_cannot_revoke_it()
    {
        var vault = await Vault.CreateAsync();
        var credential = await vault.EnrolAsync(FreshSeed());
        Assert.False(await FilingCredentialRules.RevokeAsync(credential.CredentialId, Firm, OtherAttorney, vault.Store, vault.Log, cancellationToken: Ct));
        Assert.Equal(AccessOutcome.Denied, vault.Log.Events[^1].Outcome);
        Assert.Equal(Password, (await vault.OpenAsync(credential.CredentialId)).Password);
    }

    [Fact]
    public async Task An_envelope_copied_onto_another_attorneys_item_does_not_open()
    {
        var vault = await Vault.CreateAsync();
        var victim = await vault.EnrolAsync(FreshSeed());
        var thief = await vault.EnrolAsync(FreshSeed(), attorney: OtherAttorney);
        await vault.Store.DeleteAsync(Firm, OtherAttorney, thief.CredentialId, Ct);
        await vault.Store.CreateAsync(thief with { Envelope = victim.Envelope }, Ct);
        await Assert.ThrowsAsync<CryptographicException>(() => vault.OpenAsync(thief.CredentialId, attorney: OtherAttorney));
    }

    [Theory]
    [InlineData("firm_id", OtherFirm)]
    [InlineData("attorney_id", OtherAttorney)]
    [InlineData("credential_id", "another-credential")]
    public async Task The_wrong_context_is_refused(string member, string value)
    {
        var vault = await Vault.CreateAsync();
        var credential = await vault.EnrolAsync(FreshSeed());
        var context = FilingCredentialRules.EncryptionContext(Firm, Attorney, credential.CredentialId)
            .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Key == member ? value : pair.Value))
            .ToValueDictionary();
        await Assert.ThrowsAsync<CryptographicException>(() => vault.Cipher.OpenAsync(credential.Envelope, context, Ct));
    }

    [Fact]
    public void The_context_names_the_purpose_the_key_policy_conditions_on()
    {
        Assert.Equal(
            new Dictionary<string, string> { ["purpose"] = "filing-credential", ["firm_id"] = Firm, ["attorney_id"] = Attorney, ["credential_id"] = "c1" }.ToValueDictionary(),
            FilingCredentialRules.EncryptionContext(Firm, Attorney, "c1"));

        // The key policy's condition (infra/modules/filing_credentials) names the same purpose.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var module = Path.Combine(dir.FullName, "infra", "modules", "filing_credentials", "main.tf");
            if (File.Exists(module))
            {
                Assert.Matches($"credential_purpose\\s*=\\s*\"{FilingCredentialRules.Purpose}\"", File.ReadAllText(module));
                break;
            }
        }
    }

    // test_the_item_round_trips: the item shape; dropped.

    [Fact]
    public void The_access_actions_are_registered()
    {
        var names = Wire.Names<AccessAction>();
        Assert.Contains("credential.enrol", names);
        Assert.Contains("credential.revoke", names);
        Assert.Contains("credential.open", names);
        Assert.Contains("filing.approve", names);
        Assert.Contains("filing.void", names);
        Assert.Contains("filing.consume", names);
    }

    [Fact]
    public void The_approval_rows_are_case_rows_carrying_the_filing()
    {
        var recorded = AccessEvents.Record("attorney-1", AccessAction.FilingVoid, caseId: "case-1", purpose: "changed", filingId: Filing);
        Assert.Equal(("CASE#case-1", Filing, "changed"), (recorded.SubjectKey, recorded.FilingId, recorded.Purpose));
    }

    // ── Guardrail 2: no credential without a current authorization ──

    [Fact]
    public async Task Enrolment_without_a_signed_authorization_is_refused_before_sealing()
    {
        var vault = await Vault.CreateAsync(signed: []);
        await Assert.ThrowsAsync<AuthorizationRequiredException>(() => vault.EnrolAsync(FreshSeed()));
        Assert.Empty(vault.Store.Items);
        Assert.Empty(vault.Log.Events);
        Assert.Empty(vault.Cipher.Sealed);
    }

    [Fact]
    public async Task The_credential_records_the_signature_it_was_enrolled_under()
    {
        var vault = await Vault.CreateAsync();
        var credential = await vault.EnrolAsync(FreshSeed());
        Assert.Equal((await vault.Authorizations.GetCurrentAsync(Firm, Attorney, Ct))!.AuthorizationId, credential.AuthorizationRef);
    }

    [Fact]
    public async Task Withdrawing_destroys_every_credential_and_the_next_open_fails()
    {
        var vault = await Vault.CreateAsync();
        var first = await vault.EnrolAsync(FreshSeed());
        var second = await vault.EnrolAsync(FreshSeed(), login: $"{Login}-2");
        Assert.Equal(2, await vault.WithdrawAsync());
        Assert.Empty(vault.Store.Items);
        Assert.Null(await vault.Authorizations.GetCurrentAsync(Firm, Attorney, Ct));
        await Assert.ThrowsAsync<CredentialUnavailableException>(() => vault.OpenAsync(first.CredentialId));
        Assert.Equal(
            [(AccessAction.CredentialRevoke, first.CredentialId), (AccessAction.CredentialRevoke, second.CredentialId), (AccessAction.AuthorizationWithdraw, null), (AccessAction.CredentialOpen, first.CredentialId)],
            vault.Log.Events.Skip(2).Select(e => (e.Action, e.CredentialId)));
    }

    [Fact]
    public async Task Withdrawal_marks_the_history_record_and_keeps_it()
    {
        var vault = await Vault.CreateAsync();
        var current = (await vault.Authorizations.GetCurrentAsync(Firm, Attorney, Ct))!;
        await vault.WithdrawAsync();
        var kept = vault.Authorizations.History[current.AuthorizationId];
        Assert.Equal(AuthorizationStatus.Withdrawn, kept.Status);
        Assert.NotNull(kept.WithdrawnAt);
        Assert.Equal(current.TextDigest, kept.TextDigest);
    }

    [Fact]
    public async Task A_credential_that_outlived_its_withdrawal_still_does_not_open()
    {
        var vault = await Vault.CreateAsync();
        var credential = await vault.EnrolAsync(FreshSeed());
        await vault.WithdrawAsync();
        await vault.Store.CreateAsync(credential, Ct);
        await Assert.ThrowsAsync<CredentialUnavailableException>(() => vault.OpenAsync(credential.CredentialId));
    }

    [Fact]
    public async Task Another_attorneys_withdrawal_leaves_mine_alone()
    {
        var vault = await Vault.CreateAsync();
        var mine = await vault.EnrolAsync(FreshSeed());
        await vault.EnrolAsync(FreshSeed(), attorney: OtherAttorney);
        Assert.Equal(1, await vault.WithdrawAsync(OtherAttorney));
        Assert.Equal(Password, (await vault.OpenAsync(mine.CredentialId)).Password);
    }

    [Fact]
    public async Task Withdrawing_with_nothing_signed_is_not_found()
    {
        var vault = await Vault.CreateAsync(signed: []);
        await Assert.ThrowsAsync<NotFoundException>(() => vault.WithdrawAsync());
    }

    [Fact]
    public async Task A_credential_from_before_guardrail_2_never_opens()
    {
        var vault = await Vault.CreateAsync();
        var credential = await vault.EnrolAsync(FreshSeed());
        var legacy = credential with { AuthorizationRef = null };
        await vault.Store.DeleteAsync(Firm, Attorney, credential.CredentialId, Ct);
        await vault.Store.CreateAsync(legacy, Ct);
        Assert.False(FilingCredentialRules.IsOpenable(legacy, await vault.Authorizations.GetCurrentAsync(Firm, Attorney, Ct)));
        await Assert.ThrowsAsync<CredentialUnavailableException>(() => vault.OpenAsync(credential.CredentialId));
    }

    [Fact]
    public async Task A_new_text_version_stops_opens_and_enrolments_until_it_is_signed()
    {
        var vault = await Vault.CreateAsync();
        var credential = await vault.EnrolAsync(FreshSeed());
        var held = (await vault.Authorizations.GetCurrentAsync(Firm, Attorney, Ct))!;
        vault.Authorizations.SetCurrent(held with { TextVersion = "2026-01-01-older" });
        await Assert.ThrowsAsync<CredentialUnavailableException>(() => vault.OpenAsync(credential.CredentialId));
        await Assert.ThrowsAsync<AuthorizationRequiredException>(() => vault.EnrolAsync(FreshSeed(), login: $"{Login}-2"));

        await vault.SignAsync();

        Assert.Equal(Password, (await vault.OpenAsync(credential.CredentialId)).Password);
        Assert.Equal(AuthorizationStatus.Superseded, vault.Authorizations.History[held.AuthorizationId].Status);
    }

    // The KMS sealer/opener tests (FakeKms), the table/alias name derivation
    // and the DynamoDB store's keys are the AWS adapters' — 18.12. The
    // sealer-has-no-open split is structural here: IFilingCredentialSealer
    // has no open method.
    [Fact]
    public void The_sealer_port_has_no_way_to_open() =>
        Assert.DoesNotContain(typeof(Ports.IFilingCredentialSealer).GetMethods(), method => method.Name.Contains("Open", StringComparison.Ordinal));

    [Fact]
    public async Task The_memory_store_keys_by_attorney_and_destroys_on_delete()
    {
        var vault = await Vault.CreateAsync();
        var credential = await vault.EnrolAsync(FreshSeed());
        Assert.Equal(credential, await vault.Store.GetAsync(Firm, Attorney, credential.CredentialId, Ct));
        Assert.Equal([credential], await vault.Store.ListForAttorneyAsync(Firm, Attorney, Ct));
        Assert.Empty(await vault.Store.ListForAttorneyAsync(Firm, OtherAttorney, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.Store.CreateAsync(credential, Ct));
        Assert.True(await vault.Store.DeleteAsync(Firm, Attorney, credential.CredentialId, Ct));
        Assert.False(await vault.Store.DeleteAsync(Firm, Attorney, credential.CredentialId, Ct));
        Assert.Null(await vault.Store.GetAsync(Firm, Attorney, credential.CredentialId, Ct));
    }

    private sealed class Vault
    {
        public FakeCipher Cipher { get; } = new();

        public MemoryFilingCredentialStore Store { get; } = new();

        public MemoryFilingAuthorizationStore Authorizations { get; } = new();

        public MemoryAccessLog Log { get; private set; } = new();

        public static async Task<Vault> CreateAsync(string[]? signed = null)
        {
            var vault = new Vault();
            foreach (var attorney in signed ?? [Attorney, OtherAttorney])
            {
                await vault.SignAsync(attorney);
            }

            vault.Log = new MemoryAccessLog();
            return vault;
        }

        public Task<FilingAuthorization> SignAsync(string attorney = Attorney) =>
            FilingAuthorizationRules.SignAsync(
                new JsonObject
                {
                    ["text_version"] = FilingAuthorizationRules.TextVersion,
                    ["text_digest"] = FilingAuthorizationRules.TextDigest(FilingAuthorizationRules.AuthorizationText()),
                },
                Firm,
                attorney,
                SignedInAt,
                DateTimeOffset.FromUnixTimeSeconds(SignedInAt + 30),
                Authorizations,
                Log,
                cancellationToken: Ct);

        public Task<int> WithdrawAsync(string attorney = Attorney) =>
            FilingCredentialRules.WithdrawAuthorizationAsync(Firm, attorney, Authorizations, Store, Log, cancellationToken: Ct);

        public Task<FilingCredential> EnrolAsync(string seed, string login = Login, string attorney = Attorney) =>
            FilingCredentialRules.EnrolAsync(
                FilingCredentialRules.ParseEnrolment(new JsonObject { ["login"] = login, ["password"] = Password, ["totp_seed"] = seed }),
                Firm,
                attorney,
                Cipher,
                Store,
                Log,
                Authorizations,
                cancellationToken: Ct);

        public Task<CredentialSecret> OpenAsync(string credentialId, string attorney = Attorney, string firm = Firm) =>
            FilingCredentialRules.OpenAsync(firm, attorney, credentialId, Filing, "sign_in", Cipher, Store, Authorizations, Log, cancellationToken: Ct);
    }
}
