TASK: orientation, part 2. For EACH document file listed in the INSTRUCTION (one entry per path, every path, in files[]), say what kind of document it is from its head (the first lines shown; the position says whether that is the whole file). Use the repository's own guides, which an earlier pass summarised for you in the first INSTRUCTION, and the file's own words: a file whose first line says what it is ("finished backlog rows, moved out of backlog.md") is that thing.

The kinds of document, one per file:

{{kinds}}

A file listed here may turn out not to be a document at all (a shell hook, a Dockerfile, a template, a source map, an empty placeholder). Then give one of these kinds instead:

{{not_documents}}

For each file give: path (exactly as listed), kind, title (as written in the file, or null), language (ISO 639-1 of its prose, or null when it has none), confidence (0 to 1), evidence (the line number within the text shown, and a short quote from it), reasoning (one or two sentences naming what you saw), and alternatives (other kinds you considered, or an empty list). A file whose head contradicts the guides' description of it keeps what the head says, and the reasoning says so.

The JSON shape follows.
