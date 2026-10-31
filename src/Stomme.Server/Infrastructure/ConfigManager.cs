using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stomme.Shared;

namespace Stomme.Infrastructure
{
    public interface IConfigManager
    {
        public IConfigurationSection GetSection(string sectionKey);
        public T GetSetting<T>(string settingKey, T defaultValue);
        public T GetSetting<T>(string sectionKey, string settingKey, T defaultValue);
        public Dictionary<string, string> GetSettings(string sectionKey);
        void AddOrUpdateSetting<T>(string key, T value, bool reload);
        void AddOrUpdateSetting<T>(string file, string key, T value, bool reload);
        void RemoveSetting(string key, bool reload);
        void RemoveSetting(string file, string key, bool reload);
        void Reload();

        /// <summary>
        /// The full path of the settings file <see cref="AddOrUpdateSetting{T}(string, T, bool)"/> and
        /// <see cref="RemoveSetting(string, bool)"/> write: the override file when the override directory is set, else
        /// <c>appsettings.{Environment}.json</c> when it exists outside Development, else <c>appsettings.json</c>. A pure read.
        /// </summary>
        string GetWritableSettingsPath();

        /// <summary>
        /// THIS host's data directory — <c>{ContentRootPath}/Data</c>, the value the <c>|DataDirectory|</c>
        /// connection-string token expands to.
        /// </summary>
        /// <remarks>
        /// <b>Per host, deliberately.</b> The framework used to read this from
        /// <c>AppDomain.CurrentDomain.GetData(Constants.DataDirectory)</c>, which is PROCESS-global: sound for a
        /// deployed app (one host per process) and wrong the moment two hosts coexist, as they do throughout the
        /// test suite — the last host to compose decided the directory for every other one, including hosts whose
        /// content root had already been deleted. Measured in <c>DataDirectoryIsProcessGlobalTests</c>; it is the
        /// mechanism behind a cross-suite <c>DirectoryNotFoundException</c>. Reading it off the injected
        /// <c>IWebHostEnvironment</c> makes the answer belong to the host that asks.
        /// </remarks>
        string GetDataDirectory();

        public string GetConnectionString();
        public string GetConnectionString(string name);
        public string GetDatabaseType();
        public bool IsInstalled();
        public string GetInstallationId();
        public string EnsureInstallationId();
    }

    public class ConfigManager : IConfigManager
    {
        private readonly IConfigurationRoot _config;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<ConfigManager> _filelogger;

        // Per-instance, captured at construction: several hosts run in one process (the integration suites),
        // and a static target would let the last-constructed host redirect every host's config writes into
        // its own file.
        private readonly string _appsettingsPath = "";
        private readonly string _appsettingsFilename = "";

        public ConfigManager(IConfigurationRoot config, IWebHostEnvironment environment, ILogger<ConfigManager> filelogger)
        {
            _config = config;
            _environment = environment;
            _filelogger = filelogger;

            _appsettingsPath = GetAppSettingsOverridePath();
            if (!string.IsNullOrEmpty(_appsettingsPath))
            {
                _appsettingsFilename = GetAppSettingsOverrideFilename();
            }
            if (string.IsNullOrEmpty(_appsettingsFilename))
            {
                _appsettingsPath = Directory.GetCurrentDirectory();
                _appsettingsFilename = $"appsettings.{_environment.EnvironmentName}.json";
                if (_environment.EnvironmentName == "Development" || !File.Exists(Path.Combine(_appsettingsPath, _appsettingsFilename)))
                {
                    _appsettingsFilename = "appsettings.json"; // fallback
                }
            }
        }

        /// <summary>
        /// The folder <c>STOMME_APPSETTINGS_PATH</c> names, created when it does not exist yet (BL-251, BL-239's A12: a folder or
        /// file that did not exist was silently ignored, so the installer wrote the connection string and the host password
        /// into <c>appsettings.json</c> beside the binaries instead of where the operator pointed). Null when the variable is
        /// unset, or the folder cannot be created - then the settings go beside the binaries, as before.
        /// </summary>
        public static string GetAppSettingsOverridePath()
        {
            var appSettingsOverrideFilePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
            if (string.IsNullOrEmpty(appSettingsOverrideFilePath))
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(appSettingsOverrideFilePath);
                return appSettingsOverrideFilePath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine($"{Constants.AppSettingsOverrideEnvironmentVariable} names '{appSettingsOverrideFilePath}', which cannot be created ({ex.GetType().Name}): settings are written beside the application.");
                return null;
            }
        }

        /// <summary>The override file in that folder, started as an empty object when it does not exist yet.</summary>
        public static string GetAppSettingsOverrideFilename()
        {
            var appSettingsOverrideFilePath = GetAppSettingsOverridePath();
            if (string.IsNullOrEmpty(appSettingsOverrideFilePath))
            {
                return null;
            }

            var appSettingsOverrideFileName = Path.Combine(appSettingsOverrideFilePath, "appsettings.override.json");
            try
            {
                if (!File.Exists(appSettingsOverrideFileName))
                {
                    File.WriteAllText(appSettingsOverrideFileName, "{}");
                }

                return appSettingsOverrideFileName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"'{appSettingsOverrideFileName}' cannot be created ({ex.GetType().Name}): settings are written beside the application.");
                return null;
            }
        }
        public IConfigurationSection GetSection(string key)
        {
            return _config.GetSection(key);
        }

        public T GetSetting<T>(string sectionKey, T defaultValue)
        {
            return GetSetting(sectionKey, "", defaultValue);
        }

        public T GetSetting<T>(string sectionKey, string settingKey, T defaultValue)
        {
            T value;
            if (!string.IsNullOrEmpty(settingKey))
            {
               value = _config.GetSection(sectionKey).GetValue(settingKey, defaultValue);
            }
            else
            {
                value = _config.GetValue(sectionKey, defaultValue);
            }
            if (value == null) value = defaultValue;
            return value;
        }

        public Dictionary<string, string> GetSettings(string sectionKey)
        {
            var settings = new Dictionary<string, string>();
            foreach (var kvp in _config.GetSection(sectionKey).GetChildren().AsEnumerable())
            {
                settings.Add(kvp.Key, kvp.Value);
            }
            return settings;
        }

        public void AddOrUpdateSetting<T>(string key, T value, bool reload)
        {
            AddOrUpdateSetting(_appsettingsFilename, key, value, reload);
        }

        public void AddOrUpdateSetting<T>(string file, string key, T value, bool reload)
        {
            try
            {
                var path = Path.Combine(_appsettingsPath, file);
                JsonNode node = JsonNode.Parse(File.ReadAllText(path));
                SetValueRecursively(node, key, value);
                File.WriteAllText(path, JsonSerializer.Serialize(node, new JsonSerializerOptions() { WriteIndented = true }));
                if (reload) Reload();
            }
            catch (Exception ex)
            {
                _filelogger.LogError(Utilities.LogMessage(this, $"Stomme Error: Error Updating App Setting {key} - {ex}"));
            }
        }

        public void RemoveSetting(string key, bool reload)
        {
            RemoveSetting(_appsettingsFilename, key, reload);
        }

        public void RemoveSetting(string file, string key, bool reload)
        {
            try
            {
                var path = Path.Combine(_appsettingsPath, file);
                JsonNode node = JsonNode.Parse(File.ReadAllText(path));
                RemovePropertyRecursively(node, key);
                File.WriteAllText(path, JsonSerializer.Serialize(node, new JsonSerializerOptions() { WriteIndented = true }));
                if (reload) Reload();
            }
            catch (Exception ex)
            {
                _filelogger.LogError(Utilities.LogMessage(this, $"Stomme Error: Error Removing App Setting {key} - {ex}"));
            }
        }

        private void SetValueRecursively<T>(JsonNode json, string key, T value)
        {
            if (json != null && key != null && value != null)
            {
                var remainingSections = key.Split(":", 2);

                var currentSection = remainingSections[0];
                if (remainingSections.Length > 1)
                {
                    var nextSection = remainingSections[1];
                    SetValueRecursively(json[currentSection] ??= new JsonObject(), nextSection, value);
                }
                else
                {
                    if (value.GetType() == typeof(string) && (value.ToString()!.StartsWith("[") || value.ToString()!.StartsWith("{")))
                    {
                        json[currentSection] = JsonNode.Parse(value.ToString()!);
                    }
                    else
                    {
                        json[currentSection] = JsonValue.Create(value);
                    }
                }
            }
        }

        private void RemovePropertyRecursively(JsonNode json, string key)
        {
            if (json != null && key != null)
            {
                var remainingSections = key.Split(":", 2);

                var currentSection = remainingSections[0];
                if (remainingSections.Length > 1)
                {
                    var nextSection = remainingSections[1];
                    if (json[currentSection] != null)
                    {
                        RemovePropertyRecursively(json[currentSection], nextSection);
                    }
                }
                else
                {
                    if (json.AsObject().ContainsKey(currentSection))
                    {
                        json.AsObject().Remove(currentSection);
                    }
                }
            }
        }

        public void Reload()
        {
            _config.Reload();
        }

        public string GetWritableSettingsPath()
        {
            return Path.Combine(_appsettingsPath, _appsettingsFilename);
        }

        /// <inheritdoc />
        public string GetDataDirectory()
        {
            // Matches what Program.cs has always computed - Path.Combine(ContentRootPath, "Data") - but from THIS
            // host's environment rather than a slot the whole process shares. The directory is created on demand
            // by the callers that write into it, exactly as before.
            var contentRoot = _environment?.ContentRootPath;
            return string.IsNullOrEmpty(contentRoot)
                ? AppDomain.CurrentDomain.GetData(Constants.DataDirectory)?.ToString() ?? string.Empty
                : Path.Combine(contentRoot, "Data");
        }

        public string GetConnectionString()
        {
            return _config.GetConnectionString(SettingKeys.ConnectionStringKey);
        }

        public string GetConnectionString(string name)
        {
            return _config.GetConnectionString(name);
        }

        /// <summary>
        /// Resolves the database provider to use, at read time.
        /// </summary>
        /// <remarks>
        /// The default is resolved on every read rather than persisted into appsettings.json, so that
        /// merely booting the app never mutates configuration. The value is only written back when an
        /// install is actually performed ( see DatabaseManager.UpdateDatabaseType ).
        /// </remarks>
        public string GetDatabaseType()
        {
            var databaseType = GetSetting(SettingKeys.DatabaseSection, SettingKeys.DatabaseTypeKey, "");
            if (string.IsNullOrEmpty(databaseType))
            {
                return Constants.DefaultDBType;
            }
            if (databaseType.Contains(", Stomme.Database."))
            {
                // DefaultDBType migrated to Stomme.Server in 6.1.5
                return databaseType.Substring(0, databaseType.IndexOf(", ")) + ", Stomme.Server";
            }
            return databaseType;
        }

        public bool IsInstalled()
        {
            return !string.IsNullOrEmpty(GetConnectionString());
        }

        // A PURE read. The id (and the version/date stamp) are minted only by EnsureInstallationId(),
        // which the install/upgrade path calls deliberately (DatabaseManager.UpdateInstallation) — a getter
        // that writes made every runtime read site (SystemController, PackageController) a config mutation.
        public string GetInstallationId()
        {
            return GetSetting("InstallationId", "");
        }

        public string EnsureInstallationId()
        {
            var installationid = GetSetting("InstallationId", "");
            if (installationid == "")
            {
                installationid = Guid.NewGuid().ToString();
                AddOrUpdateSetting("InstallationId", installationid, true);
            }
            var version = GetSetting("InstallationVersion", "");
            if (version != Constants.Version)
            {
                AddOrUpdateSetting("InstallationVersion", Constants.Version, true);
                AddOrUpdateSetting("InstallationDate", DateTime.UtcNow.ToString("yyyyMMddHHmm"), true);
            }
            return installationid;
        }
    }
}
