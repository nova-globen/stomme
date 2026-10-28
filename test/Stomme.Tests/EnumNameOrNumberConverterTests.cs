#nullable enable
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-251 - an API enum may be sent by name. The API read enums as numbers only, so <c>"scope": "Resource"</c> answered
    /// 400. A name (any case) or a number is read; the number is still what is written, so a client reading responses is
    /// unchanged; anything else is refused with the names it could have used.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class EnumNameOrNumberConverterTests(ITestOutputHelper output)
    {
        public enum Scope
        {
            Resource = 0,
            Field = 1,
        }

        public sealed record Body(Scope Scope, Scope? Optional);

        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new EnumNameOrNumberConverterFactory() } };

        [Theory]
        [InlineData("{\"scope\":1}")]
        [InlineData("{\"scope\":\"Field\"}")]
        [InlineData("{\"scope\":\"field\"}")]
        public void A_name_or_a_number_is_read(string json) =>
            Assert.Equal(Scope.Field, JsonSerializer.Deserialize<Body>(json, Options)!.Scope);

        [Fact]
        public void A_nullable_enum_reads_a_name_and_null_and_the_number_is_written()
        {
            Assert.Equal(Scope.Field, JsonSerializer.Deserialize<Body>("{\"scope\":0,\"optional\":\"Field\"}", Options)!.Optional);
            Assert.Null(JsonSerializer.Deserialize<Body>("{\"scope\":0,\"optional\":null}", Options)!.Optional);
            Assert.Equal("{\"scope\":1,\"optional\":null}", JsonSerializer.Serialize(new Body(Scope.Field, null), Options));
        }

        [Fact]
        public void An_unknown_name_is_refused_with_the_names_it_could_have_used()
        {
            var refused = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Body>("{\"scope\":\"Everything\"}", Options));
            Assert.Contains("Resource, Field", refused.Message, System.StringComparison.Ordinal);
        }

        [Fact]
        public async Task The_hosts_api_reads_enums_with_it()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), "localhost", output, colocateModuleAggregates: false, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var mvc = app.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value;
                Assert.Contains(mvc.JsonSerializerOptions.Converters, converter => converter is EnumNameOrNumberConverterFactory);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
