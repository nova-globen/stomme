using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Stomme.Databases.Interfaces;
// ReSharper disable ConvertToUsingDeclaration

namespace Stomme.Extensions
{
    public static class DbContextOptionsBuilderExtensions
    {
        public static DbContextOptionsBuilder UseStommeDatabase([NotNull] this DbContextOptionsBuilder optionsBuilder, IRelationalDatabase database, string connectionString)
        {
            database.UseDatabase(optionsBuilder, connectionString)
                .ConfigureWarnings(warnings => warnings.Log(RelationalEventId.PendingModelChangesWarning));

            return optionsBuilder;
        }
    }
}
