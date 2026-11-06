using System.Text.RegularExpressions;

namespace Stomme.Cartographer.Sources;

/// <summary>What the secret scan found in a file or a commit message: the rule and the line. Never the value.</summary>
public sealed record SecretHit(string Rule, int? Line)
{
    public override string ToString() => Line is null ? Rule : $"{Rule} at line {Line}";
}

/// <summary>
/// The secret scan (the owner's decision of 2026-11-05, ADR 0028): before anything leaves the machine, a file that
/// looks like it holds a secret is skipped and listed, so its content reaches neither a provider nor the bundle;
/// a commit message that does is left out the same way. The scan is by code and runs on every run, before the
/// first call. Its rules are this project's own writing: the names files of credentials carry by convention, and
/// the published shapes of keys and tokens. A hit names the rule and the line, never the value. It errs on the
/// side of withholding: a person allows a file by its path when it holds none.
/// </summary>
public static partial class SecretScan
{
    /// <summary>A text file is scanned up to this many bytes; what lies beyond is not read by any phase either.</summary>
    public const int MaxScanBytes = 8 * 1024 * 1024;

    private static readonly HashSet<string> SecretExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".pfx", ".p12", ".pkcs12", ".p8", ".jks", ".keystore", ".kdbx", ".ppk", ".ovpn", ".tfvars", ".tfstate", ".publishsettings",
    };

    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", ".pypirc", ".netrc", "_netrc", ".htpasswd", ".git-credentials", ".dockercfg", ".pgpass",
        "credentials.json", "client_secret.json", "secrets.json", "secrets.yml", "secrets.yaml", "secret.json", "secret.yml", "secret.yaml",
        "kubeconfig", "wp-config.php", "terraform.tfstate.backup",
    };

    private static readonly string[] EnvExamples = ["example", "sample", "template", "dist", "defaults", "default", "schema", "tpl"];

    private static readonly string[] PlaceholderWords =
    [
        "example", "your", "xxxx", "changeme", "change-me", "placeholder", "dummy", "redacted", "sample", "fake", "test", "todo", "insert", "replace", "secret", "password", "passwd", "foobar", "12345678", "abcdefgh",
    ];

    private static readonly string[] NotTheValue =
    [
        "name", "id", "type", "path", "file", "url", "uri", "endpoint", "header", "prefix", "suffix", "length", "expiry", "expires", "lifetime", "version", "kind", "format", "variable", "env", "field", "label", "hint", "regex", "pattern",
    ];

    private static readonly string[] KnownRules =
    [
        "private_key_block", "aws_access_key", "github_token", "gitlab_token", "slack_token", "slack_webhook", "stripe_key", "google_api_key",
        "ai_provider_key", "npm_token", "pypi_token", "sendgrid_key", "azure_storage_key", "json_web_token", "registry_auth_token",
    ];

    /// <summary>A file that holds credentials by the convention of its name: never opened by any phase.</summary>
    public static SecretHit? ByName(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var parts = relativePath.Split('/');
        var name = parts[^1];
        var lower = name.ToLowerInvariant();
        if (lower == ".env" || lower.StartsWith(".env.", StringComparison.Ordinal) || lower.EndsWith(".env", StringComparison.Ordinal))
        {
            // An example of an environment file is a document of the names, not of the values.
            return lower.Split('.', '-', '_').Any(p => EnvExamples.Contains(p, StringComparer.Ordinal)) ? null : new SecretHit("env-file", null);
        }

        if (SecretNames.Contains(name))
        {
            return new SecretHit("credential-file", null);
        }

        if (SecretExtensions.Contains(Path.GetExtension(name)))
        {
            return new SecretHit("key-file", null);
        }

        var folder = parts.Length >= 2 ? parts[^2].ToLowerInvariant() : "";
        if ((folder == ".aws" && lower == "credentials") || (folder == ".docker" && lower == "config.json") || (folder == ".ssh" && !lower.EndsWith(".pub", StringComparison.Ordinal) && lower != "known_hosts" && lower != "config"))
        {
            return new SecretHit("credential-file", null);
        }

        if (lower.EndsWith(".json", StringComparison.Ordinal) && (lower.StartsWith("service-account", StringComparison.Ordinal) || lower.StartsWith("service_account", StringComparison.Ordinal) || lower.StartsWith("serviceaccount", StringComparison.Ordinal) || lower.StartsWith("client_secret", StringComparison.Ordinal)))
        {
            return new SecretHit("credential-file", null);
        }

        return null;
    }

    /// <summary>The first thing in the text that has the shape of a secret, or null. A scan that cannot finish counts as a hit.</summary>
    public static SecretHit? InText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return null;
        }

        try
        {
            foreach (Match match in Known().Matches(text))
            {
                var rule = KnownRules.FirstOrDefault(r => match.Groups[r].Success);
                if (rule is null)
                {
                    continue;
                }

                var value = match.Groups[rule].Value;
                var settled = rule is "private_key_block" or "slack_webhook";
                if (!settled && (LooksLikeAPlaceholder(value) || (rule == "ai_provider_key" && !value.Any(char.IsAsciiDigit))))
                {
                    continue;
                }

                return new SecretHit(rule.Replace('_', '-'), LineOf(text, match.Index));
            }

            foreach (Match match in UrlPassword().Matches(text))
            {
                var password = match.Groups["pw"].Value;
                if (!LooksLikeAPlaceholder(password) && (password.Any(char.IsAsciiDigit) || password.Length >= 12))
                {
                    return new SecretHit("password-in-url", LineOf(text, match.Index));
                }
            }

            foreach (Match match in ConnectionPassword().Matches(text))
            {
                var password = match.Groups["pw"].Value;
                if (LooksLikeAPlaceholder(password) || !(password.Any(char.IsAsciiDigit) || password.Length >= 12))
                {
                    continue;
                }

                var start = text.LastIndexOf('\n', match.Index) + 1;
                var end = text.IndexOf('\n', match.Index);
                var line = text[start..(end < 0 ? text.Length : end)];
                if (ConnectionWords().IsMatch(line))
                {
                    return new SecretHit("connection-string-password", LineOf(text, match.Index));
                }
            }

            foreach (Match match in Assigned().Matches(text))
            {
                var value = match.Groups["v"].Value;
                var name = match.Groups["n"].Value.ToLowerInvariant().TrimEnd('"', '\'');
                if (name.Contains("publickeytoken", StringComparison.Ordinal) || NotTheValue.Any(w => name.EndsWith(w, StringComparison.Ordinal)))
                {
                    // The name says it holds something about the secret, not the secret: its name, its address, its kind.
                    continue;
                }

                if (LooksLikeAPlaceholder(value) || NotAValue().IsMatch(value) || !value.Any(char.IsAsciiDigit) || !value.Any(char.IsAsciiLetter) || Entropy(value) < 3.5)
                {
                    continue;
                }

                return new SecretHit("assigned-secret", LineOf(text, match.Index));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new SecretHit("scan-not-finished", null);
        }

        return null;
    }

    /// <summary>True for a value that stands for one: a variable, an elision, a word like example or changeme, or a run of few characters.</summary>
    internal static bool LooksLikeAPlaceholder(string value)
    {
        if (value.IndexOfAny(['<', '>', '{', '}', '$', '*', '%', '[', ']']) >= 0 || value.Contains("...", StringComparison.Ordinal))
        {
            return true;
        }

        var lower = value.ToLowerInvariant();
        if (PlaceholderWords.Any(w => lower.Contains(w, StringComparison.Ordinal)))
        {
            return true;
        }

        return value.Distinct().Count() < Math.Min(8, Math.Max(3, value.Length / 2));
    }

    private static double Entropy(string value)
    {
        var total = (double)value.Length;
        return value.GroupBy(c => c).Sum(g => -(g.Count() / total) * Math.Log2(g.Count() / total));
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    [GeneratedRegex(@"(?<private_key_block>-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY(?: BLOCK)?-----)|(?<aws_access_key>\b(?:AKIA|ASIA|ABIA|ACCA)[0-9A-Z]{16}\b)|(?<github_token>\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{60,}))|(?<gitlab_token>\bglpat-[A-Za-z0-9_-]{20,})|(?<slack_token>\bxox[abeprs]-[A-Za-z0-9-]{10,})|(?<slack_webhook>https://hooks\.slack\.com/services/T[A-Za-z0-9_]+/B[A-Za-z0-9_]+/[A-Za-z0-9_]{16,})|(?<stripe_key>\b[sr]k_live_[A-Za-z0-9]{20,})|(?<google_api_key>\bAIza[0-9A-Za-z_-]{35}\b)|(?<ai_provider_key>\bsk-(?:ant-|proj-|or-)?[A-Za-z0-9_-]{40,})|(?<npm_token>\bnpm_[A-Za-z0-9]{36}\b)|(?<pypi_token>\bpypi-AgEIcHlwaS5vcmc[A-Za-z0-9_-]{50,})|(?<sendgrid_key>\bSG\.[A-Za-z0-9_-]{22}\.[A-Za-z0-9_-]{43}\b)|(?<azure_storage_key>\b(?:AccountKey|SharedAccessKey)=[A-Za-z0-9+/]{40,}={0,2})|(?<json_web_token>\beyJ[A-Za-z0-9_-]{16,}\.eyJ[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,})|(?<registry_auth_token>_authToken\s*=\s*[A-Za-z0-9_+/=-]{20,})", RegexOptions.CultureInvariant, 20_000)]
    private static partial Regex Known();

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]{1,15}://[^\s/:@'""<>]{1,64}:(?<pw>[^\s/@'""<>]{6,})@[A-Za-z0-9.-]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 20_000)]
    private static partial Regex UrlPassword();

    [GeneratedRegex(@"\b(?:password|pwd)\s*=\s*(?<pw>[^;\s'""<>{}$*]{8,})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 20_000)]
    private static partial Regex ConnectionPassword();

    [GeneratedRegex(@"\b(?:server|host|data source|user id|uid|database|initial catalog)\s*=", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 5_000)]
    private static partial Regex ConnectionWords();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<n>[A-Za-z0-9_.-]{0,40}(?:api[_-]?key|secret|token|passwd|password|private[_-]?key|access[_-]?key|credential)[A-Za-z0-9_.-]{0,40})[""']?\s*(?::|=|=>|:=)\s*[""'](?<v>[^""'\s]{16,200})[""']", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 20_000)]
    private static partial Regex Assigned();

    /// <summary>What a quoted value after such a name usually is when it is no secret: an address, a path, a dotted name.</summary>
    [GeneratedRegex(@"^(?:[A-Za-z][A-Za-z0-9+.-]*://|[./~\\]|[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+$|[A-Za-z]:\\)", RegexOptions.CultureInvariant, 5_000)]
    private static partial Regex NotAValue();
}
