#nullable enable

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>What a <see cref="RunScript"/> script is written for.</summary>
    public enum ScriptStyle
    {
        /// <summary>This site's JavaScript: <c>input</c>, <c>log(...)</c>, the variables; its completion value is the result.</summary>
        JavaScript,

        /// <summary>n8n's Code node, run once for all items (and its old Function node).</summary>
        N8nAllItems,

        /// <summary>n8n's Code node, run once for each item (and its old Function Item node).</summary>
        N8nEachItem,
    }

    /// <summary>
    /// n8n's Code node, run here as it was written: the step's input - the previous step's records - as n8n's items
    /// (<c>{ json }</c>), with its <c>$input</c> (<c>all()</c>, <c>first()</c>, <c>last()</c>, <c>item</c>), <c>$json</c>, the old
    /// Function node's <c>items</c> and Function Item's <c>item</c>, and <c>console.log</c> as the step's log. Once for all items,
    /// or once for each - <c>$json</c>, <c>$input.item</c>, <c>item</c> and <c>$itemIndex</c> that item's, an item it returns
    /// nothing for kept as it was left. What it returns - items, or plain objects - are the step's records. The script is the
    /// body of a function, as n8n's is (its <c>return</c>), inside the same sandbox as any script here; n8n's other helpers
    /// (<c>$('a node')</c>, <c>$env</c>, <c>$now</c>, Luxon's <c>DateTime</c>) are not here, and a script reading one fails, named.
    /// </summary>
    internal static class N8nCode
    {
        private const string Prelude = """
            var __parsed = (function () {
              if (input === null || input === undefined || input === '') { return []; }
              try { return JSON.parse(input); } catch (e) { return input; }
            })();
            var __items = (Array.isArray(__parsed) ? __parsed : [__parsed]).map(function (j) {
              return { json: (j !== null && typeof j === 'object' && !Array.isArray(j)) ? j : { value: j } };
            });
            var items = __items;
            var console = { log: function () { log(Array.prototype.slice.call(arguments).map(function (a) { return typeof a === 'string' ? a : JSON.stringify(a); }).join(' ')); } };
            function __input(current) {
              return { all: function () { return __items; }, first: function () { return __items[0]; }, last: function () { return __items[__items.length - 1]; }, item: current };
            }
            function __records(returned) {
              return (returned === null || returned === undefined ? [] : Array.isArray(returned) ? returned : [returned]).map(function (i) {
                return i !== null && typeof i === 'object' && i.json !== undefined ? i.json : i;
              });
            }

            """;

        /// <summary>The script, wrapped so it runs as n8n ran it; the wrapped script's completion value is the records' JSON.</summary>
        public static string Wrap(string script, bool eachItem) =>
            eachItem
                ? Prelude + """
                    var __out = [];
                    for (var __i = 0; __i < __items.length; __i++) {
                      var __returned = (function ($input, $json, item, $itemIndex) {

                    """ + script + """

                      })(__input(__items[__i]), __items[__i].json, __items[__i].json, __i);
                      __out = __out.concat(__records(__returned === undefined ? __items[__i] : __returned));
                    }
                    JSON.stringify(__out)
                    """
                : Prelude + """
                    var $input = __input(__items[0]);
                    var $json = __items.length > 0 ? __items[0].json : {};
                    JSON.stringify(__records((function () {

                    """ + script + """

                    })()))
                    """;
    }
}
