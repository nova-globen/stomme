using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Application.Server
{
    public class Program
    {
        /// <summary>
        /// The app runs the framework's own host: <c>Stomme.Server.Program</c> composes the services and the middleware
        /// (reading appsettings.json, features.json and the installation's appsettings.override.json), then installs or
        /// upgrades the database and applies a staged backup restore before the first request. Calling it, rather than
        /// copying it, keeps the app on the startup of the framework version it references. Services of your own belong in
        /// a module's startup (IServerStartup), which the host discovers in this app's assemblies.
        /// </summary>
        public static async Task Main(string[] args)
        {
            KeepInstallStateOutOfAppSettings();

            var app = await Stomme.Server.Program.BuildAppAsync(args);
            var install = await Stomme.Server.Program.InstallAsync(app);
            if (!string.IsNullOrEmpty(install.Message))
            {
                app.Services.GetRequiredService<ILogger<Program>>().LogError("The installation did not complete: {Message}", install.Message);
                return;
            }

            await app.RunAsync();
        }

        /// <summary>
        /// What the installation writes - the connection string with its password, the installation id, the switches set
        /// from the admin pages - goes to <c>appsettings.override.json</c> in the directory <c>STOMME_APPSETTINGS_PATH</c>
        /// names, and to this app's <c>Data</c> folder when nothing names one. Without it the framework writes them into
        /// appsettings.json, which then could not be committed; this way appsettings.json stays the defaults the app shipped
        /// with, and <c>Data</c> - the databases' folder too - is what the app's .gitignore keeps out. The file is created
        /// when it is missing, because the framework writes the override only when it exists; a container points the
        /// variable at a mounted directory.
        /// </summary>
        private static void KeepInstallStateOutOfAppSettings()
        {
            var directory = Environment.GetEnvironmentVariable(Stomme.Shared.Constants.AppSettingsOverrideEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Path.Combine(Directory.GetCurrentDirectory(), "Data");
                Environment.SetEnvironmentVariable(Stomme.Shared.Constants.AppSettingsOverrideEnvironmentVariable, directory);
            }

            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "appsettings.override.json");
            if (!File.Exists(file))
            {
                File.WriteAllText(file, "{}");
            }
        }
    }
}
