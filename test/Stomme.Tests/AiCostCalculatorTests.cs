#nullable enable
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Stomme.Modules.Metering.Application;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The AI price list, read from the JSON a deployed install would actually write.
    /// </summary>
    /// <remarks>
    /// The calculator had no test at all, and its documented key - <c>provider:model</c> - could not be written: in
    /// configuration <c>:</c> is the PATH separator, so <c>"openai:gpt-x"</c> in JSON arrives as a section <c>openai</c>
    /// holding a section <c>gpt-x</c>. MEASURED before the fix: such a call was priced <c>0 USD</c>, silently - an
    /// under-reported bill. Every case here goes through a real JSON configuration for that reason; building the
    /// dictionary by hand is how the defect stayed invisible.
    /// </remarks>
    public class AiCostCalculatorTests
    {
        private static AiCostCalculator FromJson(string json) =>
            new(new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build());

        [Fact]
        public void A_provider_colon_model_key_written_in_JSON_prices_the_call()
        {
            var calculator = FromJson("""
                { "Metering": { "AiPricing": { "openai:gpt-x": { "InputPerMillion": 1, "OutputPerMillion": 2, "Currency": "usd" } } } }
                """);

            Assert.Equal((3m, "USD"), calculator.Price("openai", "gpt-x", 1_000_000, 1_000_000));
        }

        [Fact]
        public void The_nested_spelling_means_the_same_thing()
        {
            var calculator = FromJson("""
                { "Metering": { "AiPricing": { "OpenAI": { "GPT-X": { "InputPerMillion": 1, "OutputPerMillion": 2, "Currency": "usd" } } } } }
                """);

            // Provider and model are normalised on both sides, so the list's casing does not matter.
            Assert.Equal((3m, "USD"), calculator.Price("openai", "gpt-x", 1_000_000, 1_000_000));
        }

        [Fact]
        public void A_provider_qualified_price_wins_over_a_bare_model_and_another_provider_falls_back_to_it()
        {
            var calculator = FromJson("""
                { "Metering": { "AiPricing": {
                    "gpt-x": { "InputPerMillion": 10, "OutputPerMillion": 10, "Currency": "eur" },
                    "openai": { "gpt-x": { "InputPerMillion": 1, "OutputPerMillion": 2, "Currency": "usd" } }
                } } }
                """);

            Assert.Equal((3m, "USD"), calculator.Price("openai", "gpt-x", 1_000_000, 1_000_000));
            Assert.Equal((20m, "EUR"), calculator.Price("azure", "gpt-x", 1_000_000, 1_000_000));
        }

        [Fact]
        public void An_unpriced_model_costs_zero_and_says_so_through_the_default_currency()
        {
            var calculator = FromJson("""
                { "Metering": { "AiPricing": { "openai": { "gpt-x": { "InputPerMillion": 1 } } } } }
                """);

            Assert.Equal((0m, AiCostCalculator.DefaultCurrency), calculator.Price("openai", "gpt-y", 1_000_000, 1_000_000));
            // ...and a provider is never mistaken for a model of that name.
            Assert.Equal((0m, AiCostCalculator.DefaultCurrency), calculator.Price("anything", "openai", 1_000_000, 1_000_000));
            // One rate alone is still a price; the other is zero.
            Assert.Equal((1m, AiCostCalculator.DefaultCurrency), calculator.Price("openai", "gpt-x", 1_000_000, 1_000_000));
        }

        [Fact]
        public void A_decimal_rate_is_read_the_same_under_a_comma_decimal_culture()
        {
            var before = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
                var calculator = FromJson("""
                    { "Metering": { "AiPricing": { "gpt-x": { "InputPerMillion": 2.5, "OutputPerMillion": 0.25 } } } }
                    """);

                Assert.Equal((2.75m, AiCostCalculator.DefaultCurrency), calculator.Price("p", "gpt-x", 1_000_000, 1_000_000));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = before;
            }
        }

        [Fact]
        public void No_configuration_at_all_prices_nothing_and_does_not_throw()
        {
            Assert.Equal((0m, AiCostCalculator.DefaultCurrency), new AiCostCalculator(null).Price("p", "m", 1, 1));
        }
    }
}
