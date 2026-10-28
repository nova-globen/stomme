#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - Crypto (n8n's Crypto node), run by the engine as the host composes it: hashes and HMACs against the published
    /// test vectors, hex and base64, a random UUID and random URL-safe text, the key shown hidden in a run's data, and what it
    /// refuses saying why.
    /// </summary>
    public sealed class CryptoTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new Silent());
                module.AddStepsFrom<PipelineStepsMarker>();
            });
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class Silent : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => TextWriter.Null;
        }

        private async Task<(RunPipelineResult Run, string Result)> RunAsync(Crypto step)
        {
            using var scope = _services.CreateScope();
            var run = await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step);
            var outputs = run.Journal.StepExecutionContexts.Single(c => c.Step is Crypto).GetOutputs();
            return (run, outputs.TryGetValue("Result", out var result) ? (string)result : "");
        }

        [Theory]
        [InlineData(CryptoAlgorithm.Sha256, CryptoEncoding.Hex, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
        [InlineData(CryptoAlgorithm.Sha256, CryptoEncoding.Base64, "ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=")]
        [InlineData(CryptoAlgorithm.Sha1, CryptoEncoding.Hex, "a9993e364706816aba3e25717850c26c9cd0d89d")]
        [InlineData(CryptoAlgorithm.Md5, CryptoEncoding.Hex, "900150983cd24fb0d6963f7d28e17f72")]
        public async Task A_hash_is_the_published_vector(CryptoAlgorithm algorithm, CryptoEncoding encoding, string expected)
        {
            var (_, result) = await RunAsync(new Crypto { Value = new("abc"), Algorithm = new(algorithm), Encoding = new(encoding) });
            Assert.Equal(expected, result);
        }

        [Fact]
        public async Task An_hmac_is_the_published_vector_and_its_key_is_shown_hidden()
        {
            var (run, result) = await RunAsync(new Crypto
            {
                Id = "sign", Operation = new(CryptoOperation.Hmac), Value = new("The quick brown fox jumps over the lazy dog"), Key = new("key"),
            });
            Assert.Equal("f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8", result);

            // The key may hold a secret: the catalog marks it so, and a run's data shows such an input hidden (ListRunStepsQueryHandler).
            using var scope = _services.CreateScope();
            var catalog = ListStepsQueryHandler.Catalog(scope.ServiceProvider.GetRequiredService<IStepRegistry>());
            var entry = catalog.Single(step => step.Type == "Stomme.Crypto");
            Assert.True(entry.Inputs.Single(input => input.Name == "Key").IsSensitive);
            Assert.False(entry.Inputs.Single(input => input.Name == "Value").IsSensitive);
            Assert.Equal(PipelineSubStatus.Finished, run.PipelineState.SubStatus);
        }

        [Fact]
        public async Task A_uuid_and_random_text_are_new_each_time_and_it_refuses_what_it_cannot_do()
        {
            var first = (await RunAsync(new Crypto { Operation = new(CryptoOperation.Uuid) })).Result;
            Assert.True(Guid.TryParse(first, out _));
            Assert.NotEqual(first, (await RunAsync(new Crypto { Operation = new(CryptoOperation.Uuid) })).Result);

            var text = (await RunAsync(new Crypto { Operation = new(CryptoOperation.RandomText), Length = new(40) })).Result;
            Assert.Equal(40, text.Length);
            Assert.Matches("^[A-Za-z0-9_-]+$", text);

            var (noKey, _) = await RunAsync(new Crypto { Operation = new(CryptoOperation.Hmac), Value = new("x") });
            Assert.Equal(PipelineSubStatus.Faulted, noKey.PipelineState.SubStatus);
            Assert.Contains(noKey.PipelineState.Incidents, incident => incident.Message.Contains("Hmac needs a Key"));
        }
    }
}
