# Third-party notices

Nova Globen Stomme is source-available software of Nova Globen AB, licensed under PolyForm Noncommercial 1.0.0 OR PolyForm Small Business 1.0.0 (see `LICENSE` and `LICENSE.md`, D115). Portions of it are derived from
third-party works whose licences require that their copyright notice and permission notice be retained with
any copy or substantial portion of the affected code. Those notices are reproduced here, verbatim, and must be
kept with any distribution of Stomme that contains the affected portions. Dependencies consumed as NuGet
packages keep their own licence files inside their packages and are governed by `docs/adr/D002-permissive-licenses-only.md`.

## Oqtane Framework (harvested baseline — the runtime module/theme host, render pipeline, multi-tenancy)

Stomme's runtime module/theme host, panes and themes, render pipeline and multi-tenancy model were harvested
from the Oqtane Framework and re-owned (D1; the frozen baseline is recorded in `harvest.yaml`). The affected
files carry no per-file header; this notice covers them.

```
MIT License

Copyright (c) 2018-2026 .NET Foundation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Elsa Workflows (vendored pipeline engine — `src/Engine/` and `test/Engine/`, `Stomme.Engine.*`)

The pipeline execution engine under `src/Engine/`, and the test-side projects under `test/Engine/`, are a
vendored, renamed and modified copy of the `elsa-core` projects at tag `3.7.1` that the predecessor platform's pipeline runtime
depended on: the twelve-package transitive closure of the `Elsa` meta-package minus `Elsa.Api.Common` (dropped
whole), plus `Elsa.Testing.Shared` and seven upstream unit-test projects. The exact upstream commit
(`c7a97c64129ee5dd502f88240e51b4ffcbf9efc3`) and the full project-by-project mapping and drop list are recorded
in `src/Engine/UPSTREAM.md` (D98). The affected files carry no per-file header; this notice covers them.
Elsa Workflows is licensed under the MIT License:

```
MIT License

Copyright (c) 2021 Elsa Workflows

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Fluid (Liquid template engine — NuGet `Fluid.Core` 2.31.0, and its dependencies `Parlot` 1.5.2, `TimeZoneConverter` 7.2.0)

The notification template renderer (BL-78, D100) consumes these as NuGet packages (not vendored source). They are
recorded here because they ship inside a Stomme distribution. Licence expressions and copyright holders are as
declared in each package's `.nuspec` (verified 2026-09-14 from the local package cache); the packages carry no
licence file and no copyright year, so none is invented here. `Microsoft.Extensions.FileProviders.Abstractions`
(MIT, .NET Foundation), also a Fluid dependency, is supplied by the ASP.NET Core shared framework.

- **Fluid.Core** 2.31.0 — https://github.com/sebastienros/fluid — `MIT`, Copyright (c) Sebastien Ros.
- **Parlot** 1.5.2 — https://github.com/sebastienros/parlot — `BSD-3-Clause`, Copyright (c) Sebastien Ros.
- **TimeZoneConverter** 7.2.0 — https://github.com/mattjohnsonpint/TimeZoneConverter — `MIT`, Copyright (c) Matt Johnson-Pint.

Fluid.Core and TimeZoneConverter are licensed under the MIT License (the text reproduced above, with the
copyright line naming the holder listed for each). Parlot is licensed under the BSD 3-Clause License:

```
BSD 3-Clause License

Copyright (c) Sebastien Ros

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Markdig (Markdown processor — NuGet `Markdig` 1.3.2)

The contextual-help renderer (BL-84, D103) consumes this as a NuGet package, not vendored source. It is recorded here
because it ships inside a Stomme distribution. The licence expression and copyright holder are as declared in the
package's `.nuspec` (verified 2026-09-23 from the local package cache); on `net10.0` it has no dependencies.

- **Markdig** 1.3.2 — https://github.com/xoofx/markdig — `BSD-2-Clause`, Copyright (c) Alexandre Mutel.

```
BSD 2-Clause License

Copyright (c) Alexandre Mutel

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## AWS SDK for .NET (NuGet `AWSSDK.S3` 4.0.100.2 and its dependency `AWSSDK.Core` 4.0.100.2)

The backup module's S3-compatible archive store (BL-169) consumes these as NuGet packages, not vendored source; they are
recorded here because they ship inside a Stomme distribution. The licence expression is `Apache-2.0` and the author
Amazon Web Services, as declared in each package's `.nuspec` (verified 2026-09-29 from the packages themselves; the nuspecs
carry no copyright line). Project: https://github.com/aws/aws-sdk-net/

Licensed under the Apache License, Version 2.0; you may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0.
Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an
"AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.

## OpenAI .NET SDK (NuGet `OpenAI` 2.14.0, through `Microsoft.Extensions.AI.OpenAI` 10.10.1)

The AI assistant's OpenAI-compatible provider (BL-97 increment 9, D113) consumes these as NuGet packages, not vendored
source; they are recorded here because they ship inside a Stomme distribution. `OpenAI` is `MIT`, Copyright (c) 2026
OpenAI (https://openai.com), as declared in its `.nuspec` (verified 2026-09-30 from the package itself). Its dependency
`System.ClientModel` and the adapter `Microsoft.Extensions.AI.OpenAI` (with `Microsoft.Extensions.AI.Abstractions`, and `Microsoft.Extensions.AI` for the agents' tool-calling loop, BL-97 increment 10) are
Microsoft's, `MIT`. Project: https://github.com/openai/openai-dotnet

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation
files, to deal in the Software without restriction, subject to the MIT License's conditions; THE SOFTWARE IS PROVIDED "AS
IS", WITHOUT WARRANTY OF ANY KIND. The full text: https://opensource.org/licenses/MIT
## Model Context Protocol C# SDK (NuGet `ModelContextProtocol`, `ModelContextProtocol.AspNetCore`, `ModelContextProtocol.Core` 1.4.0)

The MCP server endpoint (BL-80, D101) consumes these as NuGet packages, not vendored source. They are recorded here
because they ship inside a Stomme distribution. The licence expression and copyright holder are as declared in each
package's `.nuspec` (verified 2026-09-22 from the packages themselves). `ModelContextProtocol.Core` arrives as a
dependency of `ModelContextProtocol`; the other two are referenced directly by `Stomme.Server`.

- **ModelContextProtocol** 1.4.0 — https://csharp.sdk.modelcontextprotocol.io/ — `Apache-2.0`, Copyright (c) Model Context Protocol, a Series of LF Projects, LLC.
- **ModelContextProtocol.AspNetCore** 1.4.0 — same project — `Apache-2.0`, same copyright holder.
- **ModelContextProtocol.Core** 1.4.0 — same project — `Apache-2.0`, same copyright holder.

Licensed under the Apache License, Version 2.0; you may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0.
Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an
"AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
