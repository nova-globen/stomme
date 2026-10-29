#nullable enable
using Stomme.Datasets;
using Stomme.Infrastructure.Governance;
using Stomme.Modules.Datasets.Application;
using Xunit;

namespace Stomme.Tests.Datasets
{
    /// <summary>The classifier cases: a secret column name is Confidential, PII in the sample is Restricted (the top of the one order), otherwise Internal; a secret name wins over PII.</summary>
    public sealed class GovernanceDataClassifierTests
    {
        private static readonly IDataClassifier Classifier = new GovernanceDataClassifier(new PiiScanner());

        [Fact]
        public void SensitiveColumnName_IsConfidential() =>
            Assert.Equal(DataClassification.Confidential, Classifier.Classify(new[] { "id", "password" }, "[ { \"id\": \"1\", \"password\": \"x\" } ]"));

        [Fact]
        public void PiiInSampleData_IsRestricted() =>
            Assert.Equal(DataClassification.Restricted, Classifier.Classify(new[] { "id", "contact" }, "[ { \"id\": \"1\", \"contact\": \"ada@example.com\" } ]"));

        [Fact]
        public void NonSensitiveData_IsInternal() =>
            Assert.Equal(DataClassification.Internal, Classifier.Classify(new[] { "sku", "quantity" }, "[ { \"sku\": \"A1\", \"quantity\": \"10\" } ]"));

        [Fact]
        public void SensitiveColumnNameTakesPrecedenceOverPii() =>
            Assert.Equal(DataClassification.Confidential, Classifier.Classify(new[] { "api_key", "email" }, "[ { \"api_key\": \"k\", \"email\": \"ada@example.com\" } ]"));

        [Theory]
        [InlineData("Password", true)]
        [InlineData("client-secret", true)]
        [InlineData("ApiKey", true)]
        [InlineData("access_token", true)]
        [InlineData("private-key", true)]
        [InlineData("tokenizer_version", true)]
        [InlineData("quantity", false)]
        [InlineData("", false)]
        public void A_secret_name_is_matched_by_its_hint_whatever_the_case(string name, bool secret) =>
            Assert.Equal(secret, GovernanceDataClassifier.IsSecretName(name));

        [Fact]
        public void A_blank_sample_and_a_card_number_are_classified_by_the_scanner()
        {
            Assert.Equal(DataClassification.Internal, Classifier.Classify(new[] { "a" }, null));
            Assert.Equal(DataClassification.Restricted, Classifier.Classify(new[] { "card" }, "[ { \"card\": \"4111 1111 1111 1111\" } ]"));
        }
    }
}
