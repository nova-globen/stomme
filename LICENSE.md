# Licence, in plain words

Nova Globen Stomme is **source-available**, not open source. You may use it free of charge under **either** of two
published licences, whichever fits you. Their full, unmodified texts are in [`LICENSE`](LICENSE); only those texts are
binding, and this page is a summary.

| You are | Licence | Cost |
|---|---|---|
| A person using it for yourself with no commercial use in view - personal study, hobby projects, private entertainment, research or testing that adds to public knowledge | [PolyForm Noncommercial 1.0.0](LICENSE) | free |
| A charity, educational institution, public research organisation, public health or safety organisation, environmental organisation or government body | [PolyForm Noncommercial 1.0.0](LICENSE) | free |
| A company with **fewer than 100** employees and contractors **and** less than **USD 1,000,000** (2019 dollars, adjusted for inflation) in revenue in the previous tax year | [PolyForm Small Business 1.0.0](LICENSE) | free |
| Anyone else - a larger company, or commercial use the two licences do not cover | a **commercial licence** from Nova Globen AB | paid; contact Nova Globen AB through <https://nova-globen.se> |

Both licences let you use, change and pass on Stomme for the purposes they allow. Whoever you pass it to must also get
the licence terms and this line, which the licences call the required notice:

    Required Notice: Copyright Nova Globen AB (https://nova-globen.se)

The applications you build on Stomme are your own; the licence covers Stomme itself. The licences grant no right to the
names Nova Globen or Stomme.

Parts of Stomme come from the Oqtane Framework and Elsa Workflows, which are MIT-licensed; their notices are in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) and stay with every copy.

## The parts that are MIT

These parts are under the **MIT licence** rather than the two licences above, so that the code you start from or copy
into your own modules and themes carries no conditions beyond MIT's:

- the **module and theme templates** the host generates new modules and themes from
  (`src/Stomme.Server/wwwroot/Modules/Templates/External` and `src/Stomme.Server/wwwroot/Themes/Templates/External`);
- the **example modules** `examples/Modules/Blogs` and `examples/Modules/KnowledgeBase`.

For these parts: Copyright Nova Globen AB. Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED
TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

A module or theme you generate from a template is yours, under whatever licence you give it; the templates start it as
MIT, which you may change. Everything else in this repository - the engine, the stock themes, the framework modules -
is under the licences above.
