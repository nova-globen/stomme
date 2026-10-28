#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.Modules.CostOptimization.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.CostOptimization
{
    /// <summary>
    /// BL-97 increment 8 - the predecessor platform's <c>ModelTierRoutingTests</c> by name (17 cases): the boundaries, the mapping and the
    /// override unchanged; the two binding cases CHANGED to the host-read section through the module's startup.
    /// </summary>
    public sealed class ModelTierRoutingTests
    {
        // --- Classification boundaries (defaults: SimpleMaxTokens 500, ComplexMinTokens 4000, ComplexMinToolCount 5) ---

        [Theory]
        [InlineData(0, ModelTier.Simple)]
        [InlineData(500, ModelTier.Simple)] // at SimpleMaxTokens → still Simple (inclusive)
        [InlineData(501, ModelTier.Medium)] // just past the Simple budget
        [InlineData(3999, ModelTier.Medium)] // just under ComplexMinTokens
        [InlineData(4000, ModelTier.Complex)] // at ComplexMinTokens (inclusive)
        public async Task Classifies_by_token_estimate_at_the_configured_boundaries(int tokenEstimate, ModelTier expected)
        {
            var decision = await BuildRouter().RouteAsync(new ModelTierRequest(tokenEstimate, ToolCount: 0, HasStructuredOutput: false));

            Assert.Equal(expected, decision.Tier);
        }

        [Theory]
        [InlineData(4, ModelTier.Simple)] // just under ComplexMinToolCount
        [InlineData(5, ModelTier.Complex)] // at ComplexMinToolCount (inclusive)
        public async Task Classifies_by_tool_count_at_the_configured_boundary(int toolCount, ModelTier expected)
        {
            var decision = await BuildRouter().RouteAsync(new ModelTierRequest(TokenEstimate: 10, toolCount, HasStructuredOutput: false));

            Assert.Equal(expected, decision.Tier);
        }

        [Fact]
        public async Task Structured_output_raises_a_simple_request_to_at_least_Medium()
        {
            var decision = await BuildRouter().RouteAsync(new ModelTierRequest(TokenEstimate: 10, ToolCount: 0, HasStructuredOutput: true));

            Assert.Equal(ModelTier.Medium, decision.Tier);
        }

        [Fact]
        public async Task Structured_output_does_not_lower_a_complex_request()
        {
            var decision = await BuildRouter().RouteAsync(new ModelTierRequest(TokenEstimate: 4000, ToolCount: 0, HasStructuredOutput: true));

            Assert.Equal(ModelTier.Complex, decision.Tier);
        }

        [Fact]
        public async Task Thresholds_are_overridable_via_options()
        {
            var router = BuildRouter(new ModelTiersOptions { SimpleMaxTokens = 100, ComplexMinTokens = 200, ComplexMinToolCount = 2 });

            Assert.Equal(ModelTier.Simple, (await router.RouteAsync(new ModelTierRequest(100, 0, false))).Tier);
            Assert.Equal(ModelTier.Medium, (await router.RouteAsync(new ModelTierRequest(101, 0, false))).Tier);
            Assert.Equal(ModelTier.Complex, (await router.RouteAsync(new ModelTierRequest(200, 0, false))).Tier);
            Assert.Equal(ModelTier.Complex, (await router.RouteAsync(new ModelTierRequest(10, 2, false))).Tier);
        }

        // --- Tier → model map resolution ---

        [Fact]
        public async Task Maps_each_tier_to_its_configured_model()
        {
            var router = BuildRouter(new ModelTiersOptions { Simple = "model-a", Medium = "model-b", Complex = "model-c" });

            Assert.Equal("model-a", (await router.RouteAsync(new ModelTierRequest(10, 0, false))).Model);
            Assert.Equal("model-b", (await router.RouteAsync(new ModelTierRequest(1000, 0, false))).Model);
            Assert.Equal("model-c", (await router.RouteAsync(new ModelTierRequest(5000, 0, false))).Model);
        }

        [Fact]
        public async Task An_unmapped_tier_resolves_with_a_null_model_so_the_caller_keeps_its_default()
        {
            var router = BuildRouter(new ModelTiersOptions { Simple = "model-a" }); // Medium/Complex unmapped

            var decision = await router.RouteAsync(new ModelTierRequest(1000, 0, false));

            Assert.Equal(ModelTier.Medium, decision.Tier);
            Assert.Null(decision.Model);
        }

        [Fact]
        public async Task A_whitespace_model_mapping_counts_as_unmapped()
        {
            var router = BuildRouter(new ModelTiersOptions { Simple = "   " });

            var decision = await router.RouteAsync(new ModelTierRequest(10, 0, false));

            Assert.Null(decision.Model);
        }

        // --- Per-request user override ---

        [Fact]
        public async Task User_override_wins_over_classification_and_maps_the_overridden_tier()
        {
            var router = BuildRouter(new ModelTiersOptions { Simple = "model-a", Complex = "model-c" });

            // Would classify Complex (tokens and tools both over the boundary) — the override forces Simple.
            var decision = await router.RouteAsync(
                new ModelTierRequest(TokenEstimate: 9000, ToolCount: 9, HasStructuredOutput: true, UserOverride: ModelTier.Simple));

            Assert.Equal(ModelTier.Simple, decision.Tier);
            Assert.Equal("model-a", decision.Model);
        }

        [Fact]
        public async Task User_override_can_also_raise_the_tier()
        {
            var router = BuildRouter(new ModelTiersOptions { Complex = "model-c" });

            var decision = await router.RouteAsync(
                new ModelTierRequest(TokenEstimate: 10, ToolCount: 0, HasStructuredOutput: false, UserOverride: ModelTier.Complex));

            Assert.Equal(ModelTier.Complex, decision.Tier);
            Assert.Equal("model-c", decision.Model);
        }

        // --- Config-map resolution through the module's startup ---
        // CHANGED: the section is CostOptimization:ModelTiers in the HOST's configuration (BL-162), composed through the module's
        // own startup; the predecessor platform's the predecessor platform:AI: prefix is dropped.

        [Fact]
        public async Task AddCostOptimization_binds_the_tier_map_and_thresholds_from_the_CostOptimization_ModelTiers_section()
        {
            await using var harness = await CostOptimizationApplicationHarness.StartAsync(new Dictionary<string, string?>
            {
                ["CostOptimization:ModelTiers:Simple"] = "cheap-model",
                ["CostOptimization:ModelTiers:Medium"] = "mid-model",
                ["CostOptimization:ModelTiers:Complex"] = "big-model",
                ["CostOptimization:ModelTiers:SimpleMaxTokens"] = "50",
            });
            var router = await harness.InScopeAsync(sp => Task.FromResult(sp.GetRequiredService<IModelTierRouter>()));

            Assert.IsType<RuleBasedModelTierRouter>(router);
            // The bound SimpleMaxTokens=50 moves the Simple/Medium boundary: 51 tokens is Medium here.
            Assert.Equal(new ModelTierDecision(ModelTier.Simple, "cheap-model"), await router.RouteAsync(new ModelTierRequest(50, 0, false)));
            Assert.Equal(new ModelTierDecision(ModelTier.Medium, "mid-model"), await router.RouteAsync(new ModelTierRequest(51, 0, false)));
            Assert.Equal(new ModelTierDecision(ModelTier.Complex, "big-model"), await router.RouteAsync(new ModelTierRequest(4000, 0, false)));
        }

        [Fact]
        public async Task AddCostOptimization_without_configuration_leaves_the_map_empty_so_routing_is_a_no_op()
        {
            await using var harness = await CostOptimizationApplicationHarness.StartAsync();
            var router = await harness.InScopeAsync(sp => Task.FromResult(sp.GetRequiredService<IModelTierRouter>()));

            var decision = await router.RouteAsync(new ModelTierRequest(10, 0, false));

            Assert.Equal(ModelTier.Simple, decision.Tier);
            Assert.Null(decision.Model);
        }

        private static RuleBasedModelTierRouter BuildRouter(ModelTiersOptions? options = null) =>
            new(options ?? new ModelTiersOptions());
    }
}
