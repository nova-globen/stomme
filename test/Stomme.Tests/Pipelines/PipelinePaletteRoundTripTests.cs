#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Helpers;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Expressions;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using EngineInput = Stomme.Engine.Pipelines.Models.Input;
using PipelineGraph = Stomme.Models.Pipelines.PipelineGraph;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// Every step the palette offers, each input given a value the editor would write, saved as the editor saves it and
    /// read back: by the editor (the same values), and by the engine (the step it is, each input holding what was given, in a
    /// form the engine can turn into the input's type). Found by building on Set Variable: its variable was saved in a shape the
    /// engine read as a variable of its own making, so every run failed - and nothing held the palette to its own save.
    /// </summary>
    public sealed class PipelinePaletteRoundTripTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddEngine(module => module.AddStepsFrom<PipelineStepsMarker>());
            services.AddExpressionDescriptorProvider<TemplateExpressionDescriptorProvider>();
            services.AddExpressionDescriptorProvider<ConditionExpressionDescriptorProvider>();
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        [Fact]
        public void Every_step_the_palette_offers_is_saved_and_read_back_with_what_its_inputs_hold()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var registry = services.GetRequiredService<IStepRegistry>();
            var serializer = services.GetRequiredService<IStepSerializer>();
            var catalog = ListStepsQueryHandler.Catalog(registry);
            var failures = new List<string>();
            var given = 0;

            foreach (var entry in catalog)
            {
                var descriptor = registry.Find(entry.Type, entry.Version)!;
                var step = new PipelineGraphStep { Id = "s", Type = entry.Type, Version = entry.Version, Name = entry.DisplayName };
                foreach (var input in entry.Inputs)
                {
                    if (input.UIHint == PipelineInputHints.VariablePicker)
                    {
                        step.InputVariables[input.Name] = "v-x";
                    }
                    else if (Sample(entry, input, descriptor.Inputs.Single(candidate => candidate.Name == input.Name).Type) is { } sample)
                    {
                        step.Inputs[input.Name] = sample;
                    }
                }

                given += step.Inputs.Count + step.InputVariables.Count;
                var graph = new PipelineGraph { Variables = { new PipelineGraphVariable { Id = "v-x", Name = "x" } }, Steps = { step } };
                string body;
                try
                {
                    body = PipelineGraphJudge.Body(graph, null, registry, services.GetRequiredService<IIdentityGenerator>(), serializer);
                }
                catch (Exception ex)
                {
                    failures.Add($"{entry.Type}: not saved - {ex.Message}");
                    continue;
                }

                // The editor reads back what it wrote.
                var back = PipelineGraphCodec.ToGraph(body).Steps.Single();
                foreach (var (name, value) in step.Inputs)
                {
                    if (!back.Inputs.TryGetValue(name, out var read) || !string.Equals(read, value, StringComparison.Ordinal))
                    {
                        failures.Add($"{entry.Type}.{name}: '{value}' read back by the editor as '{read}'");
                    }
                }

                foreach (var (name, id) in step.InputVariables)
                {
                    if (!back.InputVariables.TryGetValue(name, out var read) || read != id)
                    {
                        failures.Add($"{entry.Type}.{name}: the variable '{id}' read back by the editor as '{read}'");
                    }
                }

                // The engine reads back the step it is, each input holding what was given.
                var flowchart = (Flowchart)serializer.Deserialize(body);
                var held = flowchart.Steps.SingleOrDefault(candidate => candidate.Id == "s");
                if (held is null || held.Type != entry.Type)
                {
                    failures.Add($"{entry.Type}: read back by the engine as {held?.Type ?? "nothing"}");
                    continue;
                }

                foreach (var input in descriptor.Inputs)
                {
                    var value = input.ValueGetter(held);
                    if (step.InputVariables.TryGetValue(input.Name, out var id))
                    {
                        if (value is not Variable variable || variable.Id != id)
                        {
                            failures.Add($"{entry.Type}.{input.Name}: the engine holds {(value as Variable)?.Id ?? value?.GetType().Name ?? "nothing"}, not the variable '{id}'");
                        }
                    }
                    else if (step.Inputs.TryGetValue(input.Name, out var sample))
                    {
                        var type = Nullable.GetUnderlyingType(input.Type) ?? input.Type;
                        if (value is not EngineInput { Expression: { Type: "Literal" } expression })
                        {
                            failures.Add($"{entry.Type}.{input.Name}: the engine holds {value?.GetType().Name ?? "nothing"}, not the value given");
                            continue;
                        }

                        try
                        {
                            var converted = expression.Value.ConvertTo(type);
                            if (Comparable(type) && !string.Equals(Convert.ToString(converted, CultureInfo.InvariantCulture), sample, StringComparison.OrdinalIgnoreCase))
                            {
                                failures.Add($"{entry.Type}.{input.Name}: '{sample}' held by the engine as '{Convert.ToString(converted, CultureInfo.InvariantCulture)}'");
                            }
                        }
                        catch (Exception ex)
                        {
                            failures.Add($"{entry.Type}.{input.Name}: '{sample}' cannot be the engine's {type.Name} - {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }

            Assert.True(catalog.Count > 50 && given > 150, $"The palette held {catalog.Count} steps and {given} values were given - too few to hold it to anything.");
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        // Values compared as text with what was given: the ones the engine keeps as they are typed.
        private static bool Comparable(Type type) =>
            type == typeof(string) || type == typeof(bool) || type.IsEnum || type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(double);

        // A value each input can hold, as the editor writes one; null for an input the editor writes otherwise (a Switch's cases)
        // or whose type it offers no value for.
        private static string? Sample(StepCatalogEntry entry, StepInputView input, Type declared)
        {
            if (entry.OutcomesFrom == StepCatalogEntry.CaseOutcomes && input.Name == StepCatalogEntry.CaseOutcomes)
            {
                return null;
            }

            if (entry.OutcomesFrom == StepCatalogEntry.BranchesOutcomes && input.Name == StepCatalogEntry.BranchesOutcomes)
            {
                return PipelineOutcomes.WriteBranches(new[] { "Mail", "Log" });
            }

            switch (input.UIHint)
            {
                case PipelineInputHints.ConnectionPicker:
                    return "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
                case PipelineInputHints.RecordCondition:
                    return PipelineConditions.Write(new PipelineCondition { Rules = { new PipelineConditionRule { Left = "{{ total }}", Operator = PipelineConditions.EqualsOperator, Right = "1" } } });
                case PipelineInputHints.FormFields:
                    return PipelineForms.Serialize(new[] { new PipelineFormField { Label = "Your name", Required = true }, new PipelineFormField { Label = "Your e-mail", Type = PipelineFormFieldTypes.Email } });
                case PipelineInputHints.SiteTablePicker:
                    return "orders";
                case PipelineInputHints.AgentTools:
                    return "lookup";
                case "pipeline-definition-picker":
                    return "some-pipeline";
            }

            if (input.Options is { Count: > 0 } options)
            {
                return options[^1];
            }

            var type = Nullable.GetUnderlyingType(declared) ?? declared;
            if (type.IsGenericType && typeof(Stomme.Engine.Pipelines.Models.Input).IsAssignableFrom(type))
            {
                type = type.GetGenericArguments()[0];
                type = Nullable.GetUnderlyingType(type) ?? type;
            }

            return type switch
            {
                _ when type == typeof(bool) => "true",
                _ when type == typeof(int) || type == typeof(long) || type == typeof(short) => "3",
                _ when type == typeof(double) || type == typeof(float) || type == typeof(decimal) => "2.5",
                _ when type == typeof(TimeSpan) => "00:00:05",
                _ when type == typeof(DateTime) || type == typeof(DateTimeOffset) => "2026-01-02T03:04:05Z",
                _ when type == typeof(Guid) => "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
                _ when type == typeof(string) => (entry.Type, input.Name) switch
                {
                    ("Stomme.Schedule", "Cron") => "0 * * * *",
                    ("Stomme.Schedule", "TimeZone") => "UTC",
                    ("Stomme.Form", "Redirect") => "https://example.com/sent", // an address to go to, or the save refuses it
                    _ => "sample",
                },
                _ when typeof(IDictionary).IsAssignableFrom(type) || (type.IsGenericType && type.GetGenericTypeDefinition().Name.StartsWith("IDictionary", StringComparison.Ordinal)) => "{\"a\":\"b\"}",
                _ when typeof(IEnumerable).IsAssignableFrom(type) => "[\"a\"]",
                _ when type == typeof(object) || type.Name is "JsonElement" or "JsonNode" or "ExpandoObject" => "{\"a\":1}",
                _ => null,
            };
        }
    }
}
