#nullable enable
using System;
using System.Linq;
using System.Text.RegularExpressions;
using Stomme.Modules.Pipelines.Steps;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// The pattern half of n8n's Guardrails node: personal data held to its checksums where it has one, secret keys at
    /// each strictness, URLs against the allowed hosts and schemes, keywords as whole words, custom patterns, and the text sanitized
    /// with each finding replaced by its kind - a finding saying where it is, never what it matched.
    /// </summary>
    public sealed class TextGuardrailsTests
    {
        private static string[] Kinds(string text, GuardrailPatterns patterns) =>
            TextGuardrails.Find(text, patterns).Select(finding => finding.Kind).ToArray();

        private static GuardrailPatterns Personal(params string[] kinds) => new() { PersonalData = kinds };

        [Theory]
        [InlineData("EMAIL_ADDRESS", "Write to ada.lovelace+news@example.co.uk today.", "not-an-email@ or @example.com")]
        [InlineData("CREDIT_CARD", "Card 4111 1111 1111 1111 on file.", "Card 4111 1111 1111 1112 on file.")] // the Luhn digit
        [InlineData("IBAN_CODE", "Pay to GB82 WEST 1234 5698 7654 32 please.", "Pay to GB82 WEST 1234 5698 7654 33 please.")] // mod 97
        [InlineData("IP_ADDRESS", "From 192.168.1.10 and 2001:db8::1.", "Version 999.1.1.1 or 10.0")]
        [InlineData("US_SSN", "SSN 123-45-6789.", "SSN 000-12-3456 and 666-12-3456.")]
        [InlineData("UK_NHS", "NHS number 943 476 5919.", "NHS number 943 476 5918.")]
        [InlineData("UK_NINO", "NI AB 12 34 56 C.", "NI QQ 12 34 56 C.")]
        [InlineData("SE_PERSONAL_NUMBER", "Personnummer 19811218-9876.", "Personnummer 19811218-9877.")]
        [InlineData("FI_PERSONAL_IDENTITY_CODE", "Henkilötunnus 131052-308T.", "Henkilötunnus 131052-308U.")]
        [InlineData("PL_PESEL", "PESEL 44051401359.", "PESEL 44051401358.")]
        [InlineData("ES_NIF", "NIF 12345678Z and NIE X1234567L.", "NIF 12345678A.")]
        [InlineData("PHONE_NUMBER", "Call +46 70 174 06 05 or (555) 123-4567.", "Due 2026-10-11, order 12 34, at 192.168.100.200.")]
        [InlineData("CRYPTO", "Send to bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq.", "Send to bc1 now.")]
        public void Personal_data_is_found_only_where_it_is_held_to_its_form_and_check(string kind, string found, string notFound)
        {
            Assert.NotEmpty(Kinds(found, Personal(kind)));
            Assert.All(Kinds(found, Personal(kind)), each => Assert.Equal(kind, each));
            Assert.Empty(Kinds(notFound, Personal(kind)));
        }

        [Fact]
        public void A_long_number_is_not_a_card_because_it_is_long_and_two_findings_name_two_places()
        {
            Assert.Empty(Kinds("Order 1234567890123456 shipped.", Personal("CREDIT_CARD")));
            var findings = TextGuardrails.Find("ada@example.com, then grace@example.org", Personal("EMAIL_ADDRESS"));
            Assert.Equal(new[] { (0, 15), (22, 17) }, findings.Select(finding => (finding.Start, finding.Length)));
        }

        [Fact]
        public void Secret_keys_are_found_by_how_eager_the_check_is()
        {
            const string Known = "AWS AKIAIOSFODNN7EXAMPLE and ghp_" + "0123456789abcdefghijABCDEFGHIJ012345 and -----BEGIN RSA PRIVATE KEY-----";
            const string Assigned = "config: api_key = \"s3cr3t-v4lue-here\"";
            const string Random = "token Zx9Qm2Lp8Rt4Vy6Wb3Nc7Kd5Hf1Gj0Ts here";
            GuardrailPatterns At(SecretKeyStrictness strictness) => new() { SecretKeys = strictness };

            Assert.Equal(new[] { "AWS access key", "GitHub token", "Private key" }, Kinds(Known, At(SecretKeyStrictness.Permissive)));
            Assert.Empty(Kinds(Assigned, At(SecretKeyStrictness.Permissive)));
            Assert.Equal(new[] { "Secret value" }, Kinds(Assigned, At(SecretKeyStrictness.Balanced)));
            Assert.Empty(Kinds(Random, At(SecretKeyStrictness.Balanced)));
            Assert.Equal(new[] { "Random-looking token" }, Kinds(Random, At(SecretKeyStrictness.Strict)));
            Assert.Empty(Kinds("a_very_long_identifier_name_without_digits and /usr/local/share/applications", At(SecretKeyStrictness.Strict)));
            Assert.Empty(Kinds(Known, At(SecretKeyStrictness.Off)));
        }

        [Fact]
        public void A_url_is_found_unless_its_scheme_and_host_are_allowed()
        {
            var patterns = new GuardrailPatterns { Urls = true, AllowedHosts = new[] { "example.com", "https://partner.test/path" } };
            Assert.Empty(Kinds("See https://example.com/a, https://docs.example.com/b and https://partner.test.", patterns));
            Assert.Equal(new[] { "https" }, Kinds("See https://evil.test/login.", patterns));
            Assert.Equal(new[] { "http" }, Kinds("See http://example.com/a.", patterns)); // the scheme is not allowed
            Assert.Equal(new[] { "https" }, Kinds("See https://ada:pw@example.com/.", patterns)); // a user and password in it
            Assert.Equal(new[] { "javascript" }, Kinds("Click javascript:alert(1) now.", patterns));
            Assert.Equal(new[] { "https" }, Kinds("Visit www.evil.test today.", patterns)); // a www. address is read as https
            Assert.Equal(new[] { "https" }, Kinds("See https://docs.example.com/b.", new GuardrailPatterns { Urls = true, AllowedHosts = new[] { "example.com" }, AllowSubdomains = false }));
            Assert.Equal(new[] { "https" }, Kinds("See https://example.com/.", new GuardrailPatterns { Urls = true })); // no host allowed: every URL
            Assert.Empty(Kinds("See https://example.com/.", new GuardrailPatterns { Urls = false }));
        }

        [Fact]
        public void Keywords_are_whole_words_in_any_case_and_custom_patterns_are_named()
        {
            var patterns = new GuardrailPatterns
            {
                Keywords = new[] { "refund", "Chargeback now" },
                Custom = new[] { ("Order", new Regex(@"ORD-\d{6}", RegexOptions.None, TextGuardrails.PatternTimeout)) },
            };
            Assert.Equal(new[] { "refund", "Chargeback now", "Order" }, Kinds("A REFUND, a chargeback NOW, for ORD-123456.", patterns));
            Assert.Empty(Kinds("Refunds and refunded orders, ORD-12.", patterns));
        }

        [Fact]
        public void Sanitizing_replaces_each_finding_with_its_kind_and_never_keeps_what_it_matched()
        {
            var patterns = new GuardrailPatterns
            {
                PersonalData = new[] { "EMAIL_ADDRESS", "CREDIT_CARD" },
                SecretKeys = SecretKeyStrictness.Permissive,
                Urls = true,
                Custom = new[] { ("Order id", new Regex(@"ORD-\d{6}", RegexOptions.None, TextGuardrails.PatternTimeout)) },
            };
            const string Text = "Mail ada@example.com about ORD-123456, card 4111-1111-1111-1111, key AKIAIOSFODNN7EXAMPLE, see https://evil.test/x.";
            var findings = TextGuardrails.Find(Text, patterns);
            Assert.Equal("Mail [EMAIL_ADDRESS] about [ORDER_ID], card [CREDIT_CARD], key [SECRET_KEY], see [URL].", TextGuardrails.Sanitize(Text, findings));

            // A mailto: holding an address is one finding's place, sanitized once.
            var overlapping = new GuardrailPatterns { PersonalData = new[] { "EMAIL_ADDRESS" }, Urls = true, AllowedSchemes = new[] { "https" } };
            Assert.Equal("Write [URL] now.", TextGuardrails.Sanitize("Write mailto:ada@example.com now.", TextGuardrails.Find("Write mailto:ada@example.com now.", overlapping)));

            // A finding is a check, a kind and a place (and a model's confidence and reason): no member of it holds the text it matched.
            Assert.Equal(new[] { "Check", "Kind", "Start", "Length", "Confidence", "Reason" }, typeof(GuardrailFinding).GetProperties().Select(property => property.Name).Where(name => name != "EqualityContract"));
            Assert.Equal(Text, TextGuardrails.Sanitize(Text, Array.Empty<GuardrailFinding>()));
        }

        [Fact]
        public void The_checksums_hold()
        {
            Assert.True(TextGuardrails.Luhn("4111111111111111"));
            Assert.False(TextGuardrails.Luhn("4111111111111112"));
            Assert.True(TextGuardrails.IsIban("SE45 5000 0000 0583 9825 7466"));
            Assert.False(TextGuardrails.IsIban("SE45 5000 0000 0583 9825 7467"));
        }
    }
}
