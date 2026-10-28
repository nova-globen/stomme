#nullable enable
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Registers the file-ingestion guard (BL-100 F1, D109).</summary>
    public static class FileIngestionServiceCollectionExtensions
    {
        /// <summary>
        /// The guard (scoped - it reads site settings and folder rows), the guarded download client (singleton - its
        /// handler pools sockets), and the <see cref="NullFileScanner"/> with <c>TryAdd</c>, so a scanner registered
        /// earlier (ClamAV, a test double) wins.
        /// </summary>
        public static IServiceCollection AddStommeFileIngestion(this IServiceCollection services)
        {
            services.TryAddSingleton<IFileScanner, NullFileScanner>();
            services.TryAddSingleton<FileDownloadHttpClient>();
            services.TryAddScoped<IFileIngestionGuard, FileIngestionGuard>();
            return services;
        }
    }
}