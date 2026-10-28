using System.Text.Json;

namespace Stomme.Engine.Expressions.Models;

/// <summary>
/// Defines the context for expression serialization.
/// </summary>
public record ExpressionSerializationContext(string ExpressionType, JsonElement JsonElement, JsonSerializerOptions Options, Type MemoryBlockType);