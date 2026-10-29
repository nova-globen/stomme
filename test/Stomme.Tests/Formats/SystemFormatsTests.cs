#nullable enable
using System;
using System.Linq;
using Stomme.Formats;
using Stomme.Modules.Formats.Domain;
using Xunit;

namespace Stomme.Tests.Formats
{
    /// <summary>
    /// The compiled presets: at least thirty, all system-scoped, deterministic and unique ids,
    /// unique names within a kind, no two rendering the same way (an epoch preset that renders the same instant as the ISO UTC
    /// preset is not in the set), every example rendering, a preset found by id.
    /// </summary>
    public sealed class SystemFormatsTests
    {
        [Fact]
        public void Catalogue_HasAtLeast30Presets_AllSystemScoped()
        {
            Assert.True(SystemFormats.All.Count >= 30, $"expected 30+ presets, got {SystemFormats.All.Count}");
            Assert.All(SystemFormats.All, format => Assert.Equal(FormatScope.System, format.Scope));
        }

        [Fact]
        public void Catalogue_IdsAreDeterministicAndUnique()
        {
            var ids = SystemFormats.All.Select(format => format.Id).ToList();

            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.Equal(ids, SystemFormats.All.Select(format => format.Id).ToList());
            // Derived from the kind and the name alone - the same on every install and every provider.
            Assert.Equal(SystemFormats.IdOf(FormatKind.Date, "ISO 8601 date"), SystemFormats.All.Single(format => format.Name == "ISO 8601 date").Id);
        }

        [Fact]
        public void Catalogue_NamesAreUniqueWithinKind()
        {
            var duplicates = SystemFormats.All.GroupBy(format => (format.Kind, format.Name)).Where(group => group.Count() > 1).Select(group => group.Key).ToList();

            Assert.Empty(duplicates);
        }

        [Fact]
        public void No_two_presets_render_the_same_way()
        {
            var duplicates = SystemFormats.All.GroupBy(format => (format.Kind, format.Pattern, format.Culture)).Where(group => group.Count() > 1).Select(group => string.Join(" / ", group.Select(f => f.Name))).ToList();

            Assert.Empty(duplicates);
            Assert.DoesNotContain(SystemFormats.All, format => format.Name == "Unix epoch seconds");
        }

        [Theory]
        [InlineData("ISO 8601 date")]
        [InlineData("US date")]
        [InlineData("JPY")]
        public void Find_ResolvesPresetById(string name)
        {
            var preset = SystemFormats.All.First(format => format.Name == name);

            Assert.Equal(preset, SystemFormats.Find(preset.Id));
        }

        [Fact]
        public void Find_UnknownId_IsNull() => Assert.Null(SystemFormats.Find(Guid.NewGuid()));

        [Fact]
        public void Example_RendersWithoutThrowing_ForEveryPreset() =>
            Assert.All(SystemFormats.All, format => Assert.False(string.IsNullOrEmpty(format.Example()), format.Name));

        [Fact]
        public void Format_AppliesIsoDate()
        {
            var iso = SystemFormats.All.First(format => format.Name == "ISO 8601 date");

            Assert.Equal("2026-06-23", iso.Format(new DateTimeOffset(2026, 6, 23, 0, 0, 0, TimeSpan.Zero)));
        }
    }
}
