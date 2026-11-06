TASK: orientation, part 1. Orient yourself in this repository from its guides (CLAUDE.md, AGENTS.md, README, CONTRIBUTING, an index), the tree, and the shape of its git history, so that the later passes know what the repository is, how it names things, and where the work items, the documents and the decisions live.

Decide first what the repository is (its nature): "code" (software source in any language, with or without documents), "documentation" (a site or a folder of documents about something, such as docfx, mkdocs, Sphinx, Jekyll, Hugo or a plain folder), "design" (images, SVG, design files), "data", "mixed", or another single word you choose if none fits. Say why, with evidence.

Then report, each with evidence from the text you were shown:

- id_schemes: every scheme of identifiers the repository uses for work items, decisions, specs or sections, with an example as written (such as BL-12, #12, TASK-3, PROJ-123, D42, §J.BL-12), a regular expression that matches it (.NET syntax, with the number in a capture group when the scheme is numeric), what it refers to, and where such ids are defined. Several schemes may coexist. If you see none, give an empty list.
- status_vocabulary: the words the repository uses for an item's state (such as OPEN, DEFERRED, DONE, SUPERVISED, PROCESS, "in progress", or words in another language), each with its meaning as the guides give it and the closest of these mapped states: triage, backlog, ready, in-progress, in-review, done, cancelled, or null if none fits.
- tier_vocabulary: the words for priority or tier (such as P0 to P5, high/low, must/should) with their meaning.
- spec_index: where per-item specifications are indexed (a file and an anchor), or null.
- ledgers: the files the guides name as holding work items (open-ledger: open work; finished-ledger: finished or closed work; change-log: dated entries of what landed). Only files that exist in the tree you were shown. If the guides name none, give an empty list; the next pass looks at every document's head anyway.
- document_roots: the folders where the documents live.
- documents_thin: your judgment whether the repository's documents are few or tiny relative to what it is, with reasoning. The engine applies its own thresholds too.
- requests: up to 6 things you need to see to answer well: a file in full ("file") or a folder listing ("listing"), each with why. Ask only when the guides point at something you cannot see. You will be shown what you asked for and asked the whole task again. Give an empty list when you need nothing.
- notes: anything else the later passes should know (conventions, traps, where things moved, what the guides forbid).
- reasoning: two to five sentences on how you read the repository.

The JSON shape follows. Every field is required; use null or an empty list where there is nothing.
