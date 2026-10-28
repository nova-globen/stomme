using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Stomme.Models;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Stomme.Shared;
using Stomme.Infrastructure;
using Stomme.Enums;
using System.Net.Http.Headers;
using System.Text.Json;
using Stomme.Managers;
using System.Net;
// ReSharper disable PartialTypeWithSinglePart

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class PackageController : Controller
    {
        private readonly IInstallationManager _installationManager;
        private readonly IUserManager _userManager;
        private readonly IWebHostEnvironment _environment;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logger;

        public PackageController(IInstallationManager installationManager, IUserManager userManager, IWebHostEnvironment environment, IConfigManager configManager, ILogManager logger)
        {
            _installationManager = installationManager;
            _userManager = userManager;
            _environment = environment;
            _configManager = configManager;
            _logger = logger;
        }

        // GET: api/<controller>?type=x&search=y&price=z&package=a
        [HttpGet]
        public async Task<IEnumerable<Package>> Get(string type, string search, string price, string package, string sort)
        {
            // get packages
            List<Package> packages = new List<Package>();
            var url = _configManager.GetSetting("PackageRegistryUrl", Constants.PackageRegistryUrl);
            if (!string.IsNullOrEmpty(url))
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("Referer", HttpContext.Request.Scheme + "://" + HttpContext.Request.Host.Value);
                    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(Constants.PackageId, Constants.Version));
                    packages = await GetJson<List<Package>>(client, url + $"/api/registry/packages/?id={_configManager.GetInstallationId()}&type={type.ToLower()}&version={Constants.Version}&search={WebUtility.UrlEncode(search)}&price={price}&package={package}&sort={sort}&email={WebUtility.UrlEncode(await GetPackageRegistryEmailAsync())}");
                }
            }
            return packages;
        }

        // GET: api/<controller>/updates/?type=x
        [HttpGet("updates")]
        public async Task<IEnumerable<Package>> GetPackageUpdates(string type)
        {
            // get packages
            List<Package> packages = new List<Package>();
            var url = _configManager.GetSetting("PackageRegistryUrl", Constants.PackageRegistryUrl);
            if (!string.IsNullOrEmpty(url))
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("Referer", HttpContext.Request.Scheme + "://" + HttpContext.Request.Host.Value);
                    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(Constants.PackageId, Constants.Version));
                    packages = await GetJson<List<Package>>(client, url + $"/api/registry/updates/?id={_configManager.GetInstallationId()}&version={Constants.Version}&type={type}&email={WebUtility.UrlEncode(await GetPackageRegistryEmailAsync())}");
                }
            }
            return packages;
        }

        [HttpPost]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<Package> Post(string packageid, string version, string download, string install)
        {
            // BL-109: packageid and version are written straight into a FILE NAME under the Packages folder
            // below, so a name containing a separator or ".." would place a .nupkg outside it - which the next
            // restart would then install. Host-only, but a Host does not expect a package id to be a path.
            if (!IsPackageNameSafe(packageid) || !IsPackageNameSafe(version))
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Package Download Refused - Unsafe Package Id Or Version");
                HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return null;
            }

            // get package info
            Package package = null;
            var url = _configManager.GetSetting("PackageRegistryUrl", Constants.PackageRegistryUrl);
            if (!string.IsNullOrEmpty(url))
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("Referer", HttpContext.Request.Scheme + "://" + HttpContext.Request.Host.Value);
                    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(Constants.PackageId, Constants.Version));
                    package = await GetJson<Package>(client, url + $"/api/registry/package/?id={_configManager.GetInstallationId()}&package={packageid}&version={version}&download={download}&email={WebUtility.UrlEncode(await GetPackageRegistryEmailAsync())}");
                }

                // BL-109: a registry that refused, or answered nothing usable, used to leave a 200 with an empty body - which
                // every admin screen read as "downloaded". Now it is a 502 the client turns into an error message.
                if (package == null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Read, "Package {PackageId} {Version} Is Not Available From The Registry", packageid, version);
                    HttpContext.Response.StatusCode = FailedDependency;
                    return null;
                }

                if (bool.TryParse(install, out var installRequested) && installRequested)
                {
                    // BL-109: a package the registry names without a usable address used to reach GetAsync(null) and throw
                    // outside any try - an unhandled 500.
                    if (!Uri.TryCreate(package.PackageUrl, UriKind.Absolute, out var packageUri) || (packageUri.Scheme != Uri.UriSchemeHttps && packageUri.Scheme != Uri.UriSchemeHttp))
                    {
                        _logger.Log(LogLevel.Error, this, LogFunction.Create, "Package {PackageId} {Version} Has No Download Address", packageid, version);
                        HttpContext.Response.StatusCode = FailedDependency;
                        return null;
                    }

                    try
                    {
                        using (var httpClient = new HttpClient())
                        {
                            var folder = Path.Combine(_environment.ContentRootPath, Constants.PackagesFolder);
                            var response = await httpClient.GetAsync(packageUri).ConfigureAwait(false);
                            if (!response.IsSuccessStatusCode)
                            {
                                _logger.Log(LogLevel.Error, this, LogFunction.Create, "Could Not Download {PackageUrl} - {StatusCode}", packageUri.GetLeftPart(UriPartial.Path), (int)response.StatusCode);
                                HttpContext.Response.StatusCode = FailedDependency;
                                return null;
                            }

                            // Written beside its name and moved into place whole: the next restart installs every .nupkg in
                            // the folder, so a download cut off half-way must never be left under the package's name.
                            Directory.CreateDirectory(folder);
                            var target = Path.Combine(folder, packageid + "." + version + ".nupkg");
                            var partial = target + ".download";
                            try
                            {
                                using (var fileStream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                                {
                                    await response.Content.CopyToAsync(fileStream).ConfigureAwait(false);
                                }
                                System.IO.File.Move(partial, target, true);
                            }
                            finally
                            {
                                if (System.IO.File.Exists(partial))
                                {
                                    System.IO.File.Delete(partial);
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                    {
                        _logger.Log(LogLevel.Error, this, LogFunction.Create, ex, "Could Not Download {PackageUrl}", packageUri.GetLeftPart(UriPartial.Path));
                        HttpContext.Response.StatusCode = FailedDependency;
                        return null;
                    }
                }
            }
            else
            {
                // BL-109: no registry is configured, so nothing can be downloaded - an error, not an empty success.
                _logger.Log(LogLevel.Error, this, LogFunction.Read, "Package {PackageId} {Version} Requested With No Package Registry Configured", packageid, version);
                HttpContext.Response.StatusCode = FailedDependency;
                return null;
            }
            return package;
        }

        // BL-109 (2026-10-22, found on UAT): a failed download answers 424, Failed Dependency - not 502 or 503, which the
        // Cloudflare edge in front of a deployed site replaces with its own "Bad gateway" page, so the client never read why.
        private const int FailedDependency = 424;

        private async Task<string> GetPackageRegistryEmailAsync()
        {
            var email = _configManager.GetSetting("PackageRegistryEmail", "");
            if (string.IsNullOrEmpty(email))
            {
                if (User.Identity.IsAuthenticated)
                {
                    var user = await _userManager.GetUserAsync(User.Identity.Name, -1);
                    if (user != null)
                    {
                        email = user.Email;
                        _configManager.AddOrUpdateSetting("PackageRegistryEmail", email, true);
                    }
                }
            }
            return email;
        }

        private async Task<T> GetJson<T>(HttpClient httpClient, string url)
        {
            try
            {
                Uri uri = new Uri(url);
                var response = await httpClient.GetAsync(uri).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && ValidateJsonContent(response.Content))
                {
                    var stream = await response.Content.ReadAsStreamAsync();
                    using (var streamReader = new StreamReader(stream))
                    {
                        return await JsonSerializer.DeserializeAsync<T>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    }
                }

                // BL-109: a refusal (402, 403, any non-success) or a non-JSON answer used to return nothing with no log.
                // The path only: the query carries the installation's registry e-mail.
                _logger.Log(LogLevel.Error, this, LogFunction.Read, "Marketplace API {Url} Answered {StatusCode} {MediaType}", uri.GetLeftPart(UriPartial.Path), (int)response.StatusCode, response.Content?.Headers.ContentType?.MediaType ?? "(no content type)");
                return default(T);
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Read, ex, "Error Accessing Marketplace API {Url}", url.Split('?')[0]);
                return default(T);
            }
        }

        private static bool ValidateJsonContent(HttpContent content)
        {
            var mediaType = content?.Headers.ContentType?.MediaType;
            return mediaType != null && mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase);
        }

        [HttpGet("install")]
        [Authorize(Roles = RoleNames.Host)]
        public void InstallPackages()
        {
            _logger.Log(LogLevel.Information, this, LogFunction.Create, "Packages Installed");
            _installationManager.InstallPackages();
        }

        /// <summary>
        /// Whether a package id or version may be used in a file name (BL-109).
        /// </summary>
        /// <remarks>
        /// NuGet ids and versions are letters, digits, dot, dash, underscore and plus - nothing that can
        /// traverse or escape a path. Anything else is REFUSED rather than sanitised, because a silently
        /// rewritten name would install a package the caller did not ask for.
        /// </remarks>
        private static bool IsPackageNameSafe(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "." || value == "..")
            {
                return false;
            }

            foreach (var character in value)
            {
                if (!char.IsLetterOrDigit(character) && character != '.' && character != '-' && character != '_' && character != '+')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
