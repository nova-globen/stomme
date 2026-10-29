#nullable enable
using Stomme.Infrastructure.Governance;
using Xunit;

namespace Stomme.Tests.Datasets
{
    /// <summary>The PII scanner: each category detected and masked, clean text untouched, every occurrence in mixed text masked, a Luhn-invalid digit run not over-masked.</summary>
    public sealed class PiiScannerTests
    {
        private readonly IPiiScanner _scanner = new PiiScanner();

        [Fact]
        public void DetectsAndMasksEmail()
        {
            const string text = "contact jane.doe@example.com for access";
            var matches = _scanner.Scan(text);
            Assert.Single(matches);
            Assert.Equal(PiiCategory.Email, matches[0].Category);
            Assert.DoesNotContain("jane.doe@example.com", _scanner.Mask(text));
            Assert.Contains(RedactionMarker.Value, _scanner.Mask(text));
        }

        [Fact]
        public void DetectsAndMasksSsn()
        {
            const string text = "ssn 123-45-6789 on file";
            var matches = _scanner.Scan(text);
            Assert.Single(matches);
            Assert.Equal(PiiCategory.SocialSecurityNumber, matches[0].Category);
            Assert.DoesNotContain("123-45-6789", _scanner.Mask(text));
        }

        [Theory]
        [InlineData("4111 1111 1111 1111")]
        [InlineData("4111-1111-1111-1111")]
        [InlineData("4111111111111111")]
        public void DetectsAndMasksLuhnValidCreditCard(string card)
        {
            var text = $"card {card} charged";
            var matches = _scanner.Scan(text);
            Assert.Single(matches);
            Assert.Equal(PiiCategory.CreditCard, matches[0].Category);
            Assert.DoesNotContain(card, _scanner.Mask(text));
        }

        [Fact]
        public void DoesNotMaskLuhnInvalidDigitRun()
        {
            const string text = "order 4111111111111112 shipped";
            Assert.False(_scanner.ContainsPii(text));
            Assert.Equal(text, _scanner.Mask(text));
        }

        [Fact]
        public void MasksEveryOccurrenceInMixedText()
        {
            const string text = "email a@b.co ssn 123-45-6789 card 4111 1111 1111 1111 done";
            var masked = _scanner.Mask(text);
            Assert.Equal(3, _scanner.Scan(text).Count);
            Assert.DoesNotContain("a@b.co", masked);
            Assert.DoesNotContain("123-45-6789", masked);
            Assert.DoesNotContain("4111", masked);
        }

        [Fact]
        public void LeavesCleanTextUntouched()
        {
            const string text = "pipeline run 42 completed in 1200ms";
            Assert.False(_scanner.ContainsPii(text));
            Assert.Equal(text, _scanner.Mask(text));
        }

        [Fact]
        public void EmptyText_HasNoMatches()
        {
            Assert.Empty(_scanner.Scan(string.Empty));
            Assert.Equal(string.Empty, _scanner.Mask(string.Empty));
        }
    }
}
