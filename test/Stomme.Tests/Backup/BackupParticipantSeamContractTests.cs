#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stomme.Backup;
using Xunit;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// BL-81 (D106 item 2) - the participant seam is SERVER-FREE: it lives in <c>Stomme.Shared</c>, and every type its
    /// public surface names is the BCL's or the seam's own, so a module ships a participant referencing only
    /// <c>Stomme.Shared</c>. The scanner is validated both ways: 0 on the seam, and a finding on a planted type that names
    /// a server type.
    /// </summary>
    public class BackupParticipantSeamContractTests
    {
        [Fact]
        public void The_seam_lives_in_Shared_and_names_only_the_BCL_and_itself()
        {
            var seam = SeamTypes();
            Assert.Contains(typeof(IBackupParticipant), seam);
            Assert.Contains(typeof(BackupExcludedAttribute), seam);
            Assert.All(seam, type => Assert.Equal("Stomme.Shared", type.Assembly.GetName().Name));
            Assert.Empty(seam.SelectMany(Foreign));
        }

        [Fact]
        public void The_scanner_finds_a_server_type_on_a_planted_surface()
        {
            var found = Foreign(typeof(PlantedParticipantSurface)).ToList();
            Assert.Contains(found, finding => finding.Contains("Stomme.Modules.Backup.Application.Archive.BackupManifest", StringComparison.Ordinal));
        }

        [Fact]
        public void A_part_validation_and_an_exclusion_state_their_reasons()
        {
            Assert.True(BackupPartValidation.Valid.IsValid);
            Assert.False(BackupPartValidation.Invalid("a row names a site that is not in the snapshot").IsValid);
            Assert.Throws<ArgumentException>(() => BackupPartValidation.Invalid());
            Assert.Throws<ArgumentException>(() => new BackupExcludedAttribute(" "));
            Assert.Throws<ArgumentException>(() => new BackupScope(" ", Array.Empty<int>()));
        }

        private static List<Type> SeamTypes() =>
            typeof(IBackupParticipant).Assembly.GetExportedTypes().Where(type => type.Namespace == "Stomme.Backup").ToList();

        /// <summary>Every type named by a public member of <paramref name="type"/> that is neither the BCL's nor the seam's.</summary>
        private static IEnumerable<string> Foreign(Type type)
        {
            const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var named = new List<Type>();
            foreach (var method in type.GetMethods(Public))
            {
                named.Add(method.ReturnType);
                named.AddRange(method.GetParameters().Select(parameter => parameter.ParameterType));
            }

            named.AddRange(type.GetProperties(Public).Select(property => property.PropertyType));
            named.AddRange(type.GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType));
            named.AddRange(type.GetInterfaces());
            if (type.BaseType is not null)
            {
                named.Add(type.BaseType);
            }

            return named.SelectMany(Flatten).Where(named => !Allowed(named)).Select(named => $"{type.FullName} -> {named.FullName}").Distinct();
        }

        private static IEnumerable<Type> Flatten(Type type)
        {
            if (type.HasElementType)
            {
                return Flatten(type.GetElementType()!);
            }

            return type.IsGenericType ? new[] { type.GetGenericTypeDefinition() }.Concat(type.GetGenericArguments().SelectMany(Flatten)) : new[] { type };
        }

        private static bool Allowed(Type type) =>
            type.IsGenericParameter || type.Assembly == typeof(object).Assembly || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true
            || type.Namespace == "Stomme.Backup";

        public abstract class PlantedParticipantSurface
        {
            public abstract Stomme.Modules.Backup.Application.Archive.BackupManifest Manifest { get; }
        }
    }
}
