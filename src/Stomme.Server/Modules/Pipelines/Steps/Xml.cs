#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>Which way <see cref="Xml"/> turns its text.</summary>
    public enum XmlOperation
    {
        ToJson,
        ToXml,
    }

    /// <summary>
    /// XML (BL-229, n8n's XML node): XML into JSON - the root element a record under its name, an attribute <c>@name</c>, an
    /// element repeated under one parent a list, an element with only text its text, mixed text <c>#text</c> - or JSON into XML
    /// the same way back, under a root (a list's items as elements named so). A document with a DTD is refused, so no entity
    /// can reach a file or the network (XXE), and a document is read to at most ten million characters.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Data", "Turn XML into JSON, or JSON into XML.", DisplayName = "XML")]
    public class Xml : DataStep
    {
        public const int MaxCharacters = 10_000_000;

        [Input(Description = "ToJson | ToXml.", DefaultValue = XmlOperation.ToJson, Order = 1)]
        public Input<XmlOperation> Operation { get; set; } = new(new Literal<XmlOperation>(XmlOperation.ToJson));

        [Input(UIHint = InputUIHints.MultiLine, Description = "The XML, or the JSON. Empty: the previous step's result.", Order = 2)]
        public Input<string?>? Value { get; set; }

        [Input(DisplayName = "Root name", Description = "ToXml: the root element's name.", DefaultValue = "root", Order = 3)]
        public Input<string?>? RootName { get; set; }

        [Input(DisplayName = "Item name", Description = "ToXml: the name a list's items take when the list itself has none.", DefaultValue = "item", Order = 4)]
        public Input<string?>? ItemName { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var text = TextOrLastResult(context, Value) ?? string.Empty;
            if (Operation.GetOrDefault(context) == XmlOperation.ToXml)
            {
                JsonNode? json;
                try
                {
                    json = JsonNode.Parse(text);
                }
                catch (JsonException ex)
                {
                    await FailAsync(context, "XML_INVALID", "The value is not JSON: " + ex.Message).ConfigureAwait(false);
                    return;
                }

                var root = new XElement(Name(RootName?.GetOrDefault(context), "root"));
                Fill(root, json, Name(ItemName?.GetOrDefault(context), "item"));
                await SucceedAsync(context, root.ToString(SaveOptions.DisableFormatting)).ConfigureAwait(false);
                return;
            }

            XDocument document;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxCharacters };
                using var reader = XmlReader.Create(new StringReader(text), settings);
                document = XDocument.Load(reader);
            }
            catch (XmlException ex)
            {
                await FailAsync(context, "XML_INVALID", "The value is not XML this step reads: " + ex.Message).ConfigureAwait(false);
                return;
            }

            var result = new JsonObject { [document.Root!.Name.LocalName] = ToJson(document.Root) };
            await SucceedAsync(context, result.ToJsonString()).ConfigureAwait(false);
        }

        // An element: its text when it has only text (null when empty), else a record of its attributes, its children - a list
        // where a name repeats - and its own text as #text.
        private static JsonNode? ToJson(XElement element)
        {
            if (!element.HasAttributes && !element.HasElements)
            {
                return element.Value.Length == 0 ? null : JsonValue.Create(element.Value);
            }

            var record = new JsonObject();
            foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
            {
                record["@" + attribute.Name.LocalName] = attribute.Value;
            }

            foreach (var group in element.Elements().GroupBy(child => child.Name.LocalName))
            {
                var children = group.ToList();
                record[group.Key] = children.Count == 1 ? ToJson(children[0]) : new JsonArray(children.Select(ToJson).ToArray());
            }

            var own = string.Concat(element.Nodes().OfType<XText>().Select(node => node.Value)).Trim();
            if (own.Length > 0)
            {
                record["#text"] = own;
            }

            return record;
        }

        // The other way: a record's @-fields attributes, #text the text, a list its items as repeated elements.
        private static void Fill(XElement element, JsonNode? json, string itemName)
        {
            switch (json)
            {
                case null:
                    return;
                case JsonObject record:
                    foreach (var (key, value) in record)
                    {
                        if (key.StartsWith('@'))
                        {
                            element.SetAttributeValue(Name(key[1..], "attribute"), Records.Text(value));
                        }
                        else if (key == "#text")
                        {
                            element.Add(new XText(Records.Text(value) ?? string.Empty));
                        }
                        else if (value is JsonArray items)
                        {
                            foreach (var item in items)
                            {
                                var child = new XElement(Name(key, itemName));
                                Fill(child, item, itemName);
                                element.Add(child);
                            }
                        }
                        else
                        {
                            var child = new XElement(Name(key, itemName));
                            Fill(child, value, itemName);
                            element.Add(child);
                        }
                    }

                    return;
                case JsonArray list:
                    foreach (var item in list)
                    {
                        var child = new XElement(itemName);
                        Fill(child, item, itemName);
                        element.Add(child);
                    }

                    return;
                default:
                    element.Add(new XText(Records.Text(json) ?? string.Empty));
                    return;
            }
        }

        // A name XML allows: the given one encoded where it holds what a name cannot (a space, a leading digit).
        private static string Name(string? given, string fallback) =>
            XmlConvert.EncodeLocalName(string.IsNullOrWhiteSpace(given) ? fallback : given.Trim())!;
    }
}
