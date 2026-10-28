using Insolvia.Api.Configuration;
using Insolvia.Aws.Sealing;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Data.Vault;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Insolvia.Api.Tests;

/// <summary>
/// What the API composes for sealed data (18.12): KMS ciphers inside Lambda,
/// the local twins elsewhere — and, in BOTH shapes, a sealer for the vault
/// and no opener at all (ADR 0024: only the filing worker opens a credential).
/// </summary>
public sealed class SealingCompositionTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly (string Name, string Value)[] Lambda =
    [
        (LambdaBootRequirements.LambdaFunctionVariable, "insolvia-staging-api"),
        ("AUTH_ISSUER_URL", "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"),
        ("AUTH_CLIENT_ID", "exampleappclientid000000"),
        ("AUTH_USER_POOL_ID", "us-east-1_EXAMPLE00"),
        ("CASE_DOCUMENT_BUCKET", "insolvia-staging-case-documents-us-east-1"),
        ("AWS_REGION", "us-east-1"),

        // Never connected to: composing a store opens nothing.
        ("INSOLVIA_DB_CONNECTION_STRING", "Host=127.0.0.1;Port=1;Database=insolvia;Username=api;Password=unused"),
    ];

    [Fact]
    public void Inside_lambda_the_ciphers_are_kms_under_the_derived_aliases_and_the_vault_stores_are_postgres()
    {
        IServiceCollection? registered = null;
        using var lambda = Host(registrations => registered = registrations, ("CASE_TABLE_NAME", "insolvia-staging-cases"));
        using var scope = lambda.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.Equal("alias/insolvia-staging-cases", Assert.IsType<KmsTaxIdCipher>(services.GetRequiredService<ITaxIdCipher>()).KeyId);
        Assert.Equal("alias/insolvia-staging-filing-credentials", Assert.IsType<KmsFilingCredentialSealer>(services.GetRequiredService<IFilingCredentialSealer>()).KeyId);
        Assert.IsType<PostgresFilingCredentialStore>(services.GetRequiredService<IFilingCredentialStore>());
        Assert.IsType<PostgresFilingAuthorizationStore>(services.GetRequiredService<IFilingAuthorizationStore>());
        NoOpener(services, registered);
    }

    [Fact]
    public void Explicit_aliases_win_over_the_derivation()
    {
        using var lambda = Host(
            _ => { },
            ("CASE_TABLE_NAME", "insolvia-staging-cases"),
            (SealingKeys.CaseKeyVariable, "alias/insolvia-prod-cases"),
            (SealingKeys.FilingKeyVariable, "alias/insolvia-prod-filing-credentials"));

        Assert.Equal("alias/insolvia-prod-cases", Assert.IsType<KmsTaxIdCipher>(lambda.Services.GetRequiredService<ITaxIdCipher>()).KeyId);
        Assert.Equal("alias/insolvia-prod-filing-credentials", Assert.IsType<KmsFilingCredentialSealer>(lambda.Services.GetRequiredService<IFilingCredentialSealer>()).KeyId);
    }

    [Fact]
    public void A_lambda_that_cannot_name_its_keys_refuses_to_start()
    {
        using var lambda = Host(_ => { });

        var error = Assert.Throws<InvalidOperationException>(() => lambda.CreateClient());

        Assert.Contains(SealingKeys.FilingKeyVariable, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Elsewhere_the_ciphers_are_the_local_twins_and_still_no_opener()
    {
        IServiceCollection? registered = null;
        using var development = factory.WithWebHostBuilder(host => host.ConfigureServices(registrations => registered = registrations));
        var services = development.Services;

        Assert.IsType<LocalTaxIdCipher>(services.GetRequiredService<ITaxIdCipher>());
        Assert.IsType<LocalFilingCredentialSealer>(services.GetRequiredService<IFilingCredentialSealer>());
        Assert.IsType<MemoryFilingCredentialStore>(services.GetRequiredService<IFilingCredentialStore>());
        Assert.IsType<MemoryFilingAuthorizationStore>(services.GetRequiredService<IFilingAuthorizationStore>());
        NoOpener(services, registered);
    }

    [Theory]
    [InlineData(SealingKeys.CaseKeyVariable, "alias/aws/kms")]
    [InlineData(SealingKeys.CaseKeyVariable, "alias/insolvia-staging-filing-credentials")]
    [InlineData(SealingKeys.FilingKeyVariable, "alias/insolvia-staging-cases")]
    [InlineData(SealingKeys.FilingKeyVariable, "arn:aws:kms:us-east-1:000000000000:key/example")]
    public void An_alias_outside_the_protected_shape_is_refused(string name, string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            SealingKeys.Load(variable => variable == name ? value : variable == "CASE_TABLE_NAME" ? "insolvia-staging-cases" : null));

        Assert.StartsWith(name, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_case_table_name_that_cannot_derive_the_vault_key_is_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            SealingKeys.Load(variable => variable == "CASE_TABLE_NAME" ? "some-other-table" : null));

        Assert.Contains("CASE_TABLE_NAME cannot derive it", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// No opener resolves, and none is REGISTERED under any service type —
    /// the stronger claim: a KMS or local opener hidden behind another
    /// interface would still be a way to decrypt from the API.
    /// </summary>
    private static void NoOpener(IServiceProvider services, IServiceCollection? registered)
    {
        Assert.Null(services.GetService<IFilingCredentialOpener>());
        Assert.NotNull(registered);
        Assert.DoesNotContain(registered, descriptor =>
            descriptor.ServiceType == typeof(IFilingCredentialOpener)
            || typeof(IFilingCredentialOpener).IsAssignableFrom(descriptor.ServiceType)
            || (descriptor.ImplementationType is { } type && typeof(IFilingCredentialOpener).IsAssignableFrom(type))
            || descriptor.ImplementationInstance is IFilingCredentialOpener);
    }

    private WebApplicationFactory<Program> Host(Action<IServiceCollection> capture, params (string Name, string Value)[] extra) =>
        factory.WithWebHostBuilder(host =>
        {
            foreach (var (name, value) in Lambda.Concat(extra))
            {
                host.UseSetting(name, value);
            }

            host.ConfigureServices(capture);
        });
}
