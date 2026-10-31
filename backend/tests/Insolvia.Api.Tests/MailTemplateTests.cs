using System.Text.Json.Nodes;
using Insolvia.Api.Mail;

namespace Insolvia.Api.Tests;

/// <summary>
/// The transactional templates. Parity first: <c>Fixtures/python-mail.json</c>
/// is the Python backend's core/mail.py rendering every template (captured
/// before 18.23 deleted it), with and without an unsubscribe link, named /
/// unnamed / blank, through inputs that exercise every escape — and each part
/// of each message must match byte for byte. A deliberate template change
/// edits the fixture in the same PR. Then test_mail.py, translated.
/// </summary>
public sealed class MailTemplateTests
{
    private static readonly MailLinks Links = MailTemplates.LinksFor("https://www.insolvia.ai", "https://www.insolvia.ai/unsubscribe?token=v1.abc.def");
    private static readonly MailLinks LinksWithoutUnsubscribe = MailTemplates.LinksFor("https://www.insolvia.ai");

    private static readonly string[] UnsafeTags = ["<style", "<link", "<script", "<button"];

    private static readonly JsonObject Fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "python-mail.json")))!.AsObject();

    public static TheoryData<int> FixtureCases => [.. Enumerable.Range(0, Fixture["cases"]!.AsArray().Count)];

    public static TheoryData<string> Builders => ["welcome", "email_verification", "password_reset", "client_invitation"];

    [Theory]
    [MemberData(nameof(FixtureCases))]
    public void Every_template_renders_exactly_what_the_python_renders(int index)
    {
        var inputs = Fixture["inputs"]!.AsObject();
        var expected = Fixture["cases"]![index]!.AsObject();
        var linksName = expected["links"]!.GetValue<string>();
        var origin = inputs["marketingOrigins"]![linksName]!.GetValue<string>();
        var links = linksName == "with-unsubscribe"
            ? MailTemplates.LinksFor(origin, inputs["unsubscribeUrl"]!.GetValue<string>())
            : MailTemplates.LinksFor(origin);
        var name = inputs["names"]![expected["name"]!.GetValue<string>()]?.GetValue<string>();
        var url = inputs["url"]!.GetValue<string>();
        var email = expected["template"]!.GetValue<string>() switch
        {
            "welcome" => MailTemplates.Welcome("pat@example.test", links, url, name),
            "email_verification" => MailTemplates.EmailVerification("pat@example.test", links, url, name),
            "password_reset" => MailTemplates.PasswordReset("pat@example.test", links, url, name),
            "client_invitation" => MailTemplates.ClientInvitation("pat@example.test", links, inputs["firmName"]!.GetValue<string>(), url, name),
            var other => throw new InvalidOperationException(other),
        };

        var python = expected["email"]!.AsObject();
        Assert.Equal(python["category"]!.GetValue<string>(), email.Category);
        Assert.Equal(python["message_class"]!.GetValue<string>(), email.MessageClass);
        Assert.Equal(python["to_address"]!.GetValue<string>(), email.ToAddress);
        Assert.Equal(python["subject"]!.GetValue<string>(), email.Subject);
        Assert.Equal(python["html_body"]!.GetValue<string>(), email.HtmlBody);
        Assert.Equal(python["text_body"]!.GetValue<string>(), email.TextBody);
        Assert.Equal(python["list_unsubscribe_url"]?.GetValue<string>(), email.ListUnsubscribeUrl);
    }

    [Fact]
    public void The_fixture_covers_every_template_in_every_shape() =>
        Assert.Equal(4 * 2 * 3, Fixture["cases"]!.AsArray().Count);

    [Fact]
    public void Welcome_email_shape()
    {
        var email = MailTemplates.Welcome("ada@example.com", Links, "https://app.insolvia.ai", "Ada");

        Assert.Equal(("welcome", "transactional", "ada@example.com", "Welcome to Insolvia"), (email.Category, email.MessageClass, email.ToAddress, email.Subject));
        Assert.Contains("https://app.insolvia.ai", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("https://app.insolvia.ai", email.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Welcome_email_without_recipient_name()
    {
        var email = MailTemplates.Welcome("ada@example.com", Links, "https://app.insolvia.ai");

        Assert.Contains("Hello,", email.HtmlBody, StringComparison.Ordinal);
        Assert.NotEmpty(email.TextBody);
    }

    [Fact]
    public void Email_verification_email_shape()
    {
        var email = MailTemplates.EmailVerification("ada@example.com", Links, "https://app.insolvia.ai/verify?token=abc", "Ada");

        Assert.Equal(("email_verification", "Verify your email address"), (email.Category, email.Subject));
        Assert.Contains("https://app.insolvia.ai/verify?token=abc", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("https://app.insolvia.ai/verify?token=abc", email.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Password_reset_email_shape()
    {
        var email = MailTemplates.PasswordReset("ada@example.com", Links, "https://app.insolvia.ai/reset?token=xyz", "Ada");

        Assert.Equal(("password_reset", "Reset your Insolvia password"), (email.Category, email.Subject));
        Assert.Contains("https://app.insolvia.ai/reset?token=xyz", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("https://app.insolvia.ai/reset?token=xyz", email.TextBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("welcome")]
    [InlineData("email_verification")]
    [InlineData("password_reset")]
    public void Recipient_name_is_html_escaped(string template)
    {
        var email = Build(template, Links, "<script>alert('hi')</script> & Co");

        Assert.DoesNotContain("<script>", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("&amp; Co", email.HtmlBody, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void Html_is_email_client_safe(string template)
    {
        var html = Build(template, Links).HtmlBody;

        Assert.All(UnsafeTags, tag => Assert.DoesNotContain(tag, html, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void Footer_carries_both_links(string template)
    {
        var email = Build(template, Links);

        Assert.All(new[] { email.HtmlBody, email.TextBody }, body =>
        {
            Assert.Contains(Links.PrivacyUrl, body, StringComparison.Ordinal);
            Assert.Contains(Links.UnsubscribeUrl!, body, StringComparison.Ordinal);
        });
        Assert.Equal(Links.UnsubscribeUrl, email.ListUnsubscribeUrl);
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void No_unsubscribe_link_degrades_visibly(string template)
    {
        var email = Build(template, LinksWithoutUnsubscribe);

        Assert.Null(email.ListUnsubscribeUrl);
        Assert.DoesNotContain("Unsubscribe", email.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Unsubscribe:", email.TextBody, StringComparison.Ordinal);
        Assert.Contains(Links.PrivacyUrl, email.HtmlBody, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void Links_follow_the_environment(string template)
    {
        var email = Build(template, MailTemplates.LinksFor("https://staging-www.insolvia.ai", "https://staging-www.insolvia.ai/unsubscribe?token=t"));

        Assert.Contains("https://staging-www.insolvia.ai/privacy", email.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("https://www.insolvia.ai", email.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_invitation_email_says_who_is_asking_and_where_to_go()
    {
        var email = MailTemplates.ClientInvitation("pat@example.test", LinksWithoutUnsubscribe, "Example & Partners", "https://app.insolvia.ai/portal", "Pat");

        Assert.Equal(("client_invitation", "transactional"), (email.Category, email.MessageClass));
        Assert.Equal("Example & Partners invited you to your client portal", email.Subject);
        Assert.All(new[] { email.HtmlBody, email.TextBody }, body =>
        {
            Assert.Contains("https://app.insolvia.ai/portal", body, StringComparison.Ordinal);
            Assert.Contains("temporary password", body, StringComparison.Ordinal);
        });
        Assert.Contains("Example &amp; Partners", email.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_invitation_email_carries_no_secret()
    {
        var email = MailTemplates.ClientInvitation("pat@example.test", LinksWithoutUnsubscribe, "Example & Partners", "https://app.insolvia.ai/portal");

        var link = email.TextBody.Split("Client portal: ", 2)[1].Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.DoesNotContain("?", link, StringComparison.Ordinal);
        Assert.DoesNotContain("{####}", email.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("{username}", email.HtmlBody, StringComparison.Ordinal);
    }

    private static OutboundEmail Build(string template, MailLinks links, string? name = null) => template switch
    {
        "welcome" => MailTemplates.Welcome("a@b.com", links, "https://app.insolvia.ai", name),
        "email_verification" => MailTemplates.EmailVerification("a@b.com", links, "https://x.example", name),
        "password_reset" => MailTemplates.PasswordReset("a@b.com", links, "https://x.example", name),
        _ => MailTemplates.ClientInvitation("a@b.com", links, "Example & Partners", "https://app.insolvia.ai/portal", name),
    };
}
