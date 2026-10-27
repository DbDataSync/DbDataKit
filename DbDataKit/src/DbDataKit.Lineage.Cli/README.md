# DbDataKit.Lineage.Cli

The `lineage` noun of the shared command surface (plan 18, G04). A product adds it to its `NounRouter` with `LineageNoun.Create(source)`, where `source` returns the server's `LineageGraph`; the same verbs then run in the command line, over `--format json` and as tools of `ui mcp`.

| Verb | Answers |
| --- | --- |
| `lineage graph [--kind <kind>]` | every node and edge (optionally only the nodes of one kind and the edges between them) and the graph's fingerprint |
| `lineage impact <node> [--depth <n>]` | what the node feeds, nearest first, with the edges, the gaps and whether the depth stopped the walk |
| `lineage provenance <node> [--depth <n>]` | what feeds the node, the same way |

All three only read (`OfflineOnly`). An unknown node is exit 1 naming it; a bad option or depth is exit 2. A gap is a node whose upstream is unknown, which is not the same as having none, so it is listed apart.
