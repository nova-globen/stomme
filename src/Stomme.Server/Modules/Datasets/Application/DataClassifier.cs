#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Datasets;
using Stomme.Infrastructure.Governance;

namespace Stomme.Modules.Datasets.Application
{
    /// <summary>Derives a dataset's classification from its column names and a sample of its rows, in process - no external or AI provider. The registrar only ever ESCALATES to the derived level.</summary>
    public interface IDataClassifier
    {
        DataClassification Classify(IReadOnlyCollection<string> columnNames, string? sampleJson);
    }

    /// <summary>
    /// The module's classifier (BL-94 increment 9, ported from the predecessor platform's composition-root adapter): a column whose name
    /// hints at a secret (password, secret, api key, token, credential, private key) classifies the dataset
    /// Confidential; otherwise PII in the sample (an e-mail address, a US SSN, a Luhn-valid card number) classifies it
    /// Restricted - the top of the ONE order (Q1); otherwise Internal.
    /// </summary>
    public sealed class GovernanceDataClassifier(IPiiScanner scanner) : IDataClassifier
    {
        private static readonly string[] SecretNameHints = { "password", "secret", "apikey", "api_key", "api-key", "token", "credential", "privatekey", "private_key", "private-key" };

        public DataClassification Classify(IReadOnlyCollection<string> columnNames, string? sampleJson)
        {
            ArgumentNullException.ThrowIfNull(columnNames);
            if (columnNames.Any(IsSecretName))
            {
                return DataClassification.Confidential;
            }

            if (!string.IsNullOrWhiteSpace(sampleJson) && scanner.ContainsPii(sampleJson))
            {
                return DataClassification.Restricted;
            }

            return DataClassification.Internal;
        }

        public static bool IsSecretName(string name)
        {
            var lower = (name ?? string.Empty).ToLowerInvariant();
            return SecretNameHints.Any(hint => lower.Contains(hint, StringComparison.Ordinal));
        }
    }
}
