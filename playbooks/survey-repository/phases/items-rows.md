TASK: items, rows of a ledger. The INPUT TEXT is one chunk of a ledger of work items; the INSTRUCTION before it says which ledger and which chunk, and what the engine already saw in the chunks before this one. Turn every work item in this chunk into one row of the JSON shape, whatever shape the ledger uses: a table row, a list entry, a heading with its text. One row per item; never invent an item, never merge two; a table row cut at the chunk's edge is still one row. A legend, a header row, a note about the ledger itself and a heading are not items.

Known about this repository (from orientation; data the engine kept):

{{digest}}

The repository's own status words and what they map to (use these mappings as given; a word marked "confirmed by a person" is settled):

{{vocabulary}}

The mapped states an item can have:

{{statuses}}

For each row give: key (the item's id exactly as written in the ledger, such as BL-12 or BL-4b, or null when the ledger gives no id), ids (every id the row carries for itself, as written), title (as written, without the id and without markdown emphasis), status_as_written (the status word or phrase exactly as written, or null when the row shows none), status (the mapped state from the list above that the status word means, or null when you cannot tell), condition (what must hold for the item to proceed when its status says it waits on someone or something, such as "only with the owner" or "after 12.0.0"; else null), tier (the tier or priority as written, from the row or from the heading above it, or null), size (as written, or null), type (the kind of work the row's own words say it is, whatever language they are in: bug, feature, task, spike, chore, question, epic, decision, documentation; or null when the row's words say nothing about its kind; never from the ledger's name or the column's position alone), type_as_written (the word or phrase that says the kind, as written, or null), type_confidence (0 to 1), relations (every link to another work item the row states, in whatever words or language, each with its kind: parent when this item is part of that one, child when that one is part of this, blocker when this waits on that, blocks when that waits on this, related, duplicate, supersedes; the other item's id as written; as_written, the words that state the link; and confidence 0 to 1; an empty list when the row states none; a bare citation such as "see BL-12" is a reference, not a relation), dates (every date the row carries, each with what it marks: created, closed, decided, released, or the row's own word, as ISO 8601 dates), detail (the row's text beyond its title, as markdown, kept as written; cut it at 1,500 characters when longer and say so in reasoning), references (every id, path, anchor, version or URL the row points at, each with its kind: id, path, anchor, version or url, and the text as written), evidence (the line within the input text as shown and a short quote), confidence (0 to 1), reasoning (one or two sentences).

Also report:

- prose_work: sections of this chunk that carry work without rows (a list of deferred features, a "not scheduled" list, a wish list), each with its heading, its bullets quoted as written (each cut at 300 characters), and why you take them for work. Give an empty list when there are none. Never turn such bullets into rows yourself.
- notes: anything the engine should know about this chunk (a table cut by the chunk's edge, a legend, a row that is really a header).
- reasoning: one to three sentences on what the chunk holds.

The JSON shape follows. Every field is required; use null or an empty list where there is nothing.
