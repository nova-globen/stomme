using System.Text.Json;
using System.Text.Json.Serialization;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.LogPersistence;
using Stomme.Engine.Pipelines.LogPersistence.Strategies;
using Stomme.Engine.Pipelines.Management.Options;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Serialization.Converters;
using Humanizer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines.Runtime;

/* The following legacy JSON structure is expected to be found in the custom properties of the pipeline and step:
 * {
 *      "logPersistenceMode": {
 *          "default": "default",
 *          "inputs": { k : v },
 *          "outputs": { k: v }
 *          }
 *  }
 */

/* The following JSON structure is expected to be found in the custom properties of the pipeline and step:
 * {
 *      "logPersistenceConfig": {
 *          "default": { "evaluationMode": "Strategy", "strategyType": "Stomme.Engine.Pipelines.LogPersistence.Strategies.Inherit, Stomme.Engine.Pipelines", "expression": "..." },
 *          "inputs": { "input1" : { "evaluationMode": "Strategy", "strategyType": "Stomme.Engine.Pipelines.LogPersistence.Strategies.Inherit, Stomme.Engine.Pipelines", "expression": "..." } },
 *          "outputs": { "output1" : { "evaluationMode": "Strategy", "strategyType": "Stomme.Engine.Pipelines.LogPersistence.Strategies.Inherit, Stomme.Engine.Pipelines", "expression": "..." } }
 *          "internalState": { "evaluationMode": "Strategy", "strategyType": "Stomme.Engine.Pipelines.LogPersistence.Strategies.Inherit, Stomme.Engine.Pipelines", "expression": "..." }
 *          }
 *  }
 */
public class StepPropertyLogPersistenceEvaluator : IStepPropertyLogPersistenceEvaluator
{
    private readonly IExpressionEvaluator _expressionEvaluator;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly IDictionary<string, ILogPersistenceStrategy> _strategies;
    private readonly IOptions<ManagementOptions> _options;
    private readonly ILogger _logger;

    private const string LegacyKey = "logPersistenceMode";
    const string ConfigKey = "logPersistenceConfig";

    public StepPropertyLogPersistenceEvaluator(
        ILogPersistenceStrategyService strategyService,
        IExpressionDescriptorRegistry expressionDescriptorRegistry,
        IExpressionEvaluator expressionEvaluator,
        IOptions<ManagementOptions> options,
        ILogger<StepPropertyLogPersistenceEvaluator> logger)
    {
        _expressionEvaluator = expressionEvaluator;
        _options = options;
        _logger = logger;
        _strategies = strategyService.ListStrategies().ToDictionary(x => x.GetType().GetSimpleAssemblyQualifiedName(), x => x);
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }.WithConverters(
            new ExpressionJsonConverterFactory(expressionDescriptorRegistry),
            new JsonStringEnumConverter(),
            new ExpandoObjectConverterFactory());
    }

    public async Task<StepLogPersistenceModeMap> EvaluateLogPersistenceModesAsync(StepExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        var (legacyProps, configProps, defaultMode) = await GetPersistenceDefaultsAsync(context, cancellationToken).ConfigureAwait(false);
        var map = new StepLogPersistenceModeMap();

        await EvaluatePropertiesAsync(context, "inputs", context.StepDescriptor.Inputs, legacyProps, configProps, defaultMode, map.Inputs, cancellationToken).ConfigureAwait(false);
        await EvaluatePropertiesAsync(context, "outputs", context.StepDescriptor.Outputs, legacyProps, configProps, defaultMode, map.Outputs, cancellationToken).ConfigureAwait(false);
        map.InternalState = await EvaluateInternalStateModeAsync(context.ExpressionExecutionContext, context.Step.CustomProperties, defaultMode, cancellationToken).ConfigureAwait(false);

        return map;
    }

    public async Task<Dictionary<string, object>> GetPersistableOutputAsync(StepExecutionContext context)
    {
        var cancellationToken = context.PipelineExecutionContext.CancellationToken;
        var (legacyProps, configProps, defaultMode) = await GetPersistenceDefaultsAsync(context, cancellationToken).ConfigureAwait(false);
        var outputs = context.GetOutputs();
        return await GetPersistablePropertiesAsync(context, outputs, "outputs", legacyProps, configProps, defaultMode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(IDictionary<string, object> legacyProps, IDictionary<string, object> configProps, LogPersistenceMode defaultMode)> GetPersistenceDefaultsAsync(StepExecutionContext context, CancellationToken cancellationToken)
    {
        var legacyProps = context.Step.CustomProperties.GetValueOrDefault<IDictionary<string, object>>(LegacyKey, () => new Dictionary<string, object>())!;
        var rootContext = context.PipelineExecutionContext.StepExecutionContexts.First(x => x.ParentStepExecutionContext == null);
        var pipeline = (Pipeline?)context.GetAncestors().FirstOrDefault(x => x.Step is Pipeline)?.Step ?? context.PipelineExecutionContext.Pipeline;
        var pipelineDefault = await GetDefaultPersistenceModeAsync(rootContext.ExpressionExecutionContext, pipeline.CustomProperties, () => _options.Value.LogPersistenceMode, cancellationToken).ConfigureAwait(false);
        var stepDefault = await GetDefaultPersistenceModeAsync(context.ExpressionExecutionContext, context.Step.CustomProperties, () => pipelineDefault, cancellationToken).ConfigureAwait(false);
        var configProps = context.Step.CustomProperties.GetValueOrDefault<IDictionary<string, object>>(ConfigKey, () => new Dictionary<string, object>())!;
        return (legacyProps, configProps, stepDefault);
    }

    private async Task EvaluatePropertiesAsync(
        StepExecutionContext context,
        string key,
        IEnumerable<PropertyDescriptor> descriptors,
        IDictionary<string, object> legacyConfig,
        IDictionary<string, object> currentConfig,
        LogPersistenceMode defaultMode,
        IDictionary<string, LogPersistenceMode> resultMap,
        CancellationToken cancellationToken)
    {
        var legacySection = legacyConfig.GetValueOrDefault(key, () => new Dictionary<string, object>())!;
        var currentSection = currentConfig.GetValueOrDefault(key, () => new Dictionary<string, object>())!;

        foreach (var descriptor in descriptors)
        {
            resultMap[descriptor.Name] = await EvaluatePropertyModeAsync(context.ExpressionExecutionContext, descriptor, legacySection, currentSection, defaultMode, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<LogPersistenceMode> EvaluatePropertyModeAsync(
        ExpressionExecutionContext executionContext,
        PropertyDescriptor descriptor,
        IDictionary<string, object> legacySection,
        IDictionary<string, object> currentSection,
        LogPersistenceMode defaultMode,
        CancellationToken cancellationToken)
    {
        var key = descriptor.Name.Camelize();
        var configObject = currentSection.GetValueOrDefault(key, () => null);
        var config = ConvertToConfig(configObject);
        if (config != null)
            return await EvaluateConfigAsync(config, executionContext, () => defaultMode, cancellationToken).ConfigureAwait(false);

        var mode = legacySection.GetValueOrDefault(key, () => defaultMode);
        return ResolveMode(mode, () => defaultMode);
    }
    
    private async Task<LogPersistenceMode> EvaluateInternalStateModeAsync(
        ExpressionExecutionContext executionContext,
        IDictionary<string, object> currentConfig,
        LogPersistenceMode defaultMode,
        CancellationToken cancellationToken)
    {
        var configObject = currentConfig.GetValueOrDefault("internalState", () => new Dictionary<string, object>())!;
        var config = ConvertToConfig(configObject);
        if (config != null) return await EvaluateConfigAsync(config, executionContext, () => defaultMode, cancellationToken).ConfigureAwait(false);
        return LogPersistenceMode.Inherit;
    }

    private async Task<Dictionary<string, object>> GetPersistablePropertiesAsync(
        StepExecutionContext context,
        IDictionary<string, object> state,
        string key,
        IDictionary<string, object> legacyConfig,
        IDictionary<string, object> currentConfig,
        LogPersistenceMode defaultMode,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, object>();
        var legacySection = legacyConfig.GetValueOrDefault(key, () => new Dictionary<string, object>());
        var currentSection = currentConfig.GetValueOrDefault(key, () => new Dictionary<string, object>());

        foreach (var item in state)
        {
            var propKey = item.Key.Camelize();
            var configObject = currentSection!.GetValueOrDefault(propKey, () => null);
            var config = ConvertToConfig(configObject);
            var mode = config != null
                ? await EvaluateConfigAsync(config, context.ExpressionExecutionContext, () => defaultMode, cancellationToken).ConfigureAwait(false)
                : legacySection!.GetValueOrDefault(propKey, () => defaultMode);

            if (mode == LogPersistenceMode.Include || (mode == LogPersistenceMode.Inherit && (defaultMode == LogPersistenceMode.Include || defaultMode == LogPersistenceMode.Inherit)))
                result.Add(item.Key, item.Value);
        }

        return result;
    }

    private async Task<LogPersistenceMode> GetDefaultPersistenceModeAsync(
        ExpressionExecutionContext executionContext,
        IDictionary<string, object> properties,
        Func<LogPersistenceMode> defaultFactory,
        CancellationToken cancellationToken)
    {
        var legacyProps = properties.GetValueOrDefault<IDictionary<string, object>>(LegacyKey, () => new Dictionary<string, object>());
        var configProps = properties.GetValueOrDefault<IDictionary<string, object>>(ConfigKey, () => new Dictionary<string, object>());
        var defaultObj = configProps!.TryGetValue("default", out var val) ? val : null;
        if (defaultObj == null)
        {
            var legacyDefault = legacyProps!.GetValueOrDefault("default", defaultFactory);
            return legacyDefault == LogPersistenceMode.Inherit ? defaultFactory() : legacyDefault;
        }

        var config = ConvertToConfig(defaultObj);
        return await EvaluateConfigAsync(config, executionContext, defaultFactory, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LogPersistenceMode> EvaluateConfigAsync(
        LogPersistenceConfiguration? config,
        ExpressionExecutionContext executionContext,
        Func<LogPersistenceMode> defaultFactory,
        CancellationToken cancellationToken)
    {
        if (config?.EvaluationMode == LogPersistenceEvaluationMode.Strategy)
        {
            var strategyType = config.StrategyType ?? typeof(Inherit).GetSimpleAssemblyQualifiedName();
            if (!_strategies.TryGetValue(strategyType, out var strategy))
                return defaultFactory();

            var strategyContext = new LogPersistenceStrategyContext(cancellationToken);
            var mode = await strategy.GetPersistenceModeAsync(strategyContext).ConfigureAwait(false);
            return ResolveMode(mode, defaultFactory);
        }

        if (config?.Expression == null)
            return defaultFactory();

        try
        {
            var mode = await _expressionEvaluator.EvaluateAsync<LogPersistenceMode>(config.Expression, executionContext).ConfigureAwait(false);
            return ResolveMode(mode, defaultFactory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error evaluating log persistence expression");
            return defaultFactory();
        }
    }

    private LogPersistenceMode ResolveMode(LogPersistenceMode mode, Func<LogPersistenceMode> defaultFactory)
    {
        var m = mode == LogPersistenceMode.Inherit ? defaultFactory() : mode;
        return m == LogPersistenceMode.Inherit ? LogPersistenceMode.Include : m;
    }

    private LogPersistenceConfiguration? ConvertToConfig(object? value)
    {
        if (value == null) return null;
        if (value is LogPersistenceConfiguration config) return config;
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<LogPersistenceConfiguration>(json, _jsonOptions);
    }
}