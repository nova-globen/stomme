#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stomme.Enums;
using Stomme.FeatureManagement.Application;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Licensing;
using Stomme.Models;
using Stomme.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using LicenceKeyStatusModel = Stomme.Models.LicenceKeyStatus;

namespace Stomme.Controllers
{
    /// <summary>
    /// The Host-only licensing surface (D105 design item 10): the status of every configured <c>Licensing:Keys:&lt;lid&gt;</c>
    /// entry, adding a pasted <c>SLK1</c> key, and removing one. It exists only under <c>Deployment:Mode=OnPrem</c>: every
    /// action checks the mode FIRST and answers 404 otherwise - there is no conditional controller mapping in this host
    /// (refutation 4), so the check lives in the action path. Authorization still runs before any of this (falsification
    /// F-J): anonymous is 403, an Administrator is 403 in every mode, and the 404 is visible only to a Host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A write is proven by READ-BACK</b> (refutation 7). <see cref="IConfigManager.AddOrUpdateSetting{T}(string, T, bool)"/>
    /// and <see cref="IConfigManager.RemoveSetting(string, bool)"/> return void and swallow every exception into a log, and
    /// the documented Docker path mounts <c>appsettings.Production.json</c> read-only - so after every write this controller
    /// reads the merged <see cref="IConfigurationRoot"/> back and, when the value in force is not what it wrote, answers
    /// <b>500 ProblemDetails naming the settings file and the reason</b>, never a false 200. 500 (not 409) because the
    /// request was valid and the failure is the installation's configuration storage; 409 is reserved for DELETE of a key a
    /// non-writable source supplies, which is a state the operator must change elsewhere.
    /// </para>
    /// <para>
    /// Keys are a MAP written under ONE canonical name, the <c>lid</c> as <c>Guid.ToString("D")</c> (lower-case): JSON object
    /// names are case-sensitive in <c>ConfigManager</c>'s writer and remover, while configuration lookup is not. A DELETE
    /// removes the entry under the exact casing its source file stores.
    /// </para>
    /// <para>
    /// Logs carry the <c>lid</c> and the reason only, never a key. Every action is synchronous (the verifier and the
    /// configuration writes are synchronous), so there is nothing to block on (D33).
    /// </para>
    /// </remarks>
    [Route(ControllerRoutes.ApiRoute)]
    public class LicenceController : Controller
    {
        private const string KeyPrefix = OfflineLicenceEntitlementProvider.KeysSection + ":";

        // IConfigurationRoot, NOT IConfiguration: the merged root AddStomme registers (with the override file and every
        // layered source) - the same reason OpenIddictController and AdminSiteTemplate inject it.
        private readonly IConfigurationRoot _configuration;
        private readonly IConfigManager _configManager;
        private readonly IServiceProvider _services;
        private readonly ILogManager _logger;

        public LicenceController(IConfigurationRoot configuration, IConfigManager configManager, IServiceProvider services, ILogManager logger)
        {
            _configuration = configuration;
            _configManager = configManager;
            _services = services;
            _logger = logger;
        }

        private bool Enabled => DeploymentMode.IsOnPrem(_configuration);

        // GET: api/Licence
        [HttpGet]
        [Authorize(Roles = RoleNames.Host)]
        public IActionResult Get()
        {
            if (!Enabled)
            {
                return NotFound();
            }

            return Ok(BuildStatus());
        }

        // POST: api/Licence  - verify, write, read back.
        [HttpPost]
        [Authorize(Roles = RoleNames.Host)]
        public IActionResult Post([FromBody] LicenceKeySubmission? submission)
        {
            if (!Enabled)
            {
                return NotFound();
            }

            var blob = submission?.Key?.Trim() ?? string.Empty;
            var installationId = _configManager.GetInstallationId() ?? string.Empty;
            var clock = _services.GetService<TimeProvider>() ?? TimeProvider.System;

            // Verify FIRST: nothing is written for a key that would not be in force.
            var verification = LicenceKeyVerifier.Verify(blob, installationId, clock.GetUtcNow());
            if (!verification.IsValid || verification.Payload is null)
            {
                _logger.Log(LogLevel.Warning, this, LogFunction.Create, "Licence key rejected: lid {Lid}, reason {Reason}",
                    verification.Payload?.Lid.ToString("D") ?? "(unparsed)", verification.Reason);
                return BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "The licence key was rejected.",
                    Detail = verification.Reason.ToString(),
                    Extensions = { ["reason"] = verification.Reason.ToString() }
                });
            }

            var lid = verification.Payload.Lid.ToString("D");
            var configKey = KeyPrefix + lid;
            _configManager.AddOrUpdateSetting(configKey, blob, true);

            var readBack = _configuration[configKey];
            if (!string.Equals(readBack, blob, StringComparison.Ordinal))
            {
                var detail = DescribeWriteFailure(configKey, readBack, removing: false);
                _logger.Log(LogLevel.Error, this, LogFunction.Create, "Licence key {Lid} not saved: {Detail}", lid, detail);
                return Problem(detail: detail, statusCode: StatusCodes.Status500InternalServerError, title: "The licence key was not saved.");
            }

            _logger.Log(LogLevel.Information, this, LogFunction.Create, "Licence key {Lid} added, reason {Reason}", lid, verification.Reason);
            return Ok(BuildStatus());
        }

        // DELETE: api/Licence/{name}  - the entry's name under Licensing:Keys (its lid).
        [HttpDelete("{name}")]
        [Authorize(Roles = RoleNames.Host)]
        public IActionResult Delete(string name)
        {
            if (!Enabled)
            {
                return NotFound();
            }

            var entry = FindEntryName(name);
            if (entry is null)
            {
                return NotFound(new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "No such licence key.",
                    Detail = $"No licence key named '{name}' is configured."
                });
            }

            var configKey = KeyPrefix + entry;
            var source = SourceOf(configKey);
            if (!source.Deletable)
            {
                _logger.Log(LogLevel.Warning, this, LogFunction.Delete, "Licence key {Lid} not removed: supplied by {Source}", entry, source.Name);
                return Conflict(new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "The licence key cannot be removed here.",
                    Detail = $"The licence key '{entry}' is supplied by {source.Name}, which this page cannot change. " +
                             $"Remove it from {source.Name} instead."
                });
            }

            _configManager.RemoveSetting(ExactKey(source.Provider!, configKey) ?? configKey, true);

            var readBack = _configuration[configKey];
            if (!string.IsNullOrEmpty(readBack))
            {
                var detail = DescribeWriteFailure(configKey, readBack, removing: true);
                _logger.Log(LogLevel.Error, this, LogFunction.Delete, "Licence key {Lid} not removed: {Detail}", entry, detail);
                return Problem(detail: detail, statusCode: StatusCodes.Status500InternalServerError, title: "The licence key was not removed.");
            }

            _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Licence key {Lid} removed", entry);
            return Ok(BuildStatus());
        }

        private LicenceStatus BuildStatus()
        {
            var installationId = _configManager.GetInstallationId() ?? string.Empty;

            // Registered only under OnPrem, and EntitlementStartupValidation guarantees exactly this one provider there.
            var provider = _services.GetServices<IEntitlementProvider>().OfType<OfflineLicenceEntitlementProvider>().SingleOrDefault()
                ?? throw new InvalidOperationException("Deployment:Mode is OnPrem but no OfflineLicenceEntitlementProvider is registered.");

            var status = new LicenceStatus
            {
                Enabled = true,
                InstallationId = installationId,
                InstallationInitialised = !string.IsNullOrEmpty(installationId)
            };

            foreach (var key in provider.GetKeys())
            {
                var source = SourceOf(KeyPrefix + key.Name);
                status.Keys.Add(new LicenceKeyStatusModel
                {
                    Name = key.Name,
                    Lid = key.Lid ?? (Guid.TryParse(key.Name, out var named) ? named : Guid.Empty),
                    Label = key.Label ?? string.Empty,
                    Reason = key.Reason.ToString(),
                    Entitlements = key.Entitlements.ToList(),
                    Limits = key.Limits.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                    Expires = key.Expires,
                    Source = source.Name,
                    Deletable = source.Deletable
                });
            }

            return status;
        }

        private string? FindEntryName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var isGuid = Guid.TryParse(name, out var requested);
            foreach (var child in _configuration.GetSection(OfflineLicenceEntitlementProvider.KeysSection).GetChildren())
            {
                if (string.Equals(child.Key, name, StringComparison.OrdinalIgnoreCase)
                    || (isGuid && Guid.TryParse(child.Key, out var existing) && existing == requested))
                {
                    return child.Key;
                }
            }

            return null;
        }

        private readonly record struct KeySource(string Name, bool Deletable, IConfigurationProvider? Provider);

        /// <summary>The provider whose value is in force: the LAST provider (highest precedence) that has the key.</summary>
        private KeySource SourceOf(string configKey)
        {
            foreach (var provider in _configuration.Providers.Reverse())
            {
                if (provider.TryGet(configKey, out _))
                {
                    return Describe(provider);
                }
            }

            return new KeySource("(none)", false, null);
        }

        private KeySource Describe(IConfigurationProvider provider)
        {
            switch (provider)
            {
                case FileConfigurationProvider file:
                {
                    var name = Path.GetFileName(file.Source.Path ?? string.Empty);
                    var fullPath = file.Source.FileProvider?.GetFileInfo(file.Source.Path ?? string.Empty)?.PhysicalPath ?? file.Source.Path;
                    return new KeySource(name, SamePath(fullPath, _configManager.GetWritableSettingsPath()), provider);
                }
                case EnvironmentVariablesConfigurationProvider:
                    return new KeySource("environment", false, provider);
                default:
                    return new KeySource(provider.GetType().Name, false, provider);
            }
        }

        private static bool SamePath(string? left, string? right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }

            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The key spelled exactly as <paramref name="provider"/> stores it, segment by segment - ConfigManager's JSON remover
        /// matches object names case-sensitively, while configuration lookup matched case-insensitively.
        /// </summary>
        private static string? ExactKey(IConfigurationProvider provider, string configKey)
        {
            string? parent = null;
            foreach (var segment in configKey.Split(':'))
            {
                var match = provider.GetChildKeys(Array.Empty<string>(), parent)
                    .FirstOrDefault(child => string.Equals(child, segment, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    return null;
                }

                parent = parent is null ? match : parent + ":" + match;
            }

            return parent;
        }

        private string DescribeWriteFailure(string configKey, string? valueInForce, bool removing)
        {
            var writable = Path.GetFileName(_configManager.GetWritableSettingsPath());
            if (string.IsNullOrEmpty(valueInForce))
            {
                return $"The settings file {writable} could not be written (read-only?), or this host's configuration does not load it.";
            }

            var source = SourceOf(configKey);
            if (source.Deletable)
            {
                return $"The settings file {writable} could not be written (read-only?).";
            }

            return removing
                ? $"The key was removed from {writable}, but {source.Name} also supplies it, so it is still in force."
                : $"The key was written to {writable}, but {source.Name} supplies a different value for it, which takes precedence.";
        }
    }
}