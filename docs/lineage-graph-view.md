# The lineage graph view: which library (plan 19, Y04)

Measured on 2026-11-01 with the versions below, on one machine (Node, no browser). The graph is 500 nodes and 870 edges in eight layers, left to right, the shape of a lineage graph: many sources, a few sinks, nodes 180 × 40. The sizes are what a bundler (esbuild, minified) gives for what a page would load, gzipped, with React left out (the console has it already).

| | React Flow + dagre | Cytoscape.js + dagre | ELK.js (layered) |
| --- | --- | --- | --- |
| What it is | A React component library for node-based UIs; **no layout of its own**, a layout library is added | A graph library: its own canvas renderer, layouts as plugins | A layout engine only (Eclipse Layout Kernel in JavaScript); draws nothing |
| Licence | MIT, MIT | MIT, MIT | EPL-2.0 or GPL-3.0-or-later |
| Version | `@xyflow/react` 12.12.0, `@dagrejs/dagre` 3.1.3 | `cytoscape` 3.34.4, `cytoscape-dagre` 4.0.1 | `elkjs` 0.12.1 |
| Size, gzipped | 61 KB + 3 KB CSS + 17 KB dagre = **81 KB** | **159 KB** (renderer and layout) | **450 KB** (the engine bundled with its worker) |
| Layout of the 500-node graph | dagre: ~1.0 s | dagre in Cytoscape: ~1.0 s | ELK: **0.4 to 0.7 s** |
| Edge crossings of that layout | 11,236 | 11,176 | **10,367** (8% fewer) |
| A node is | any React component: a card with a title, a kind badge, columns that fold, a run mark | a canvas shape with a label; rich content needs overlays | not applicable |
| Many nodes | renders only what is in view (`onlyRenderVisibleElements`), DOM per node | canvas, the best of the three at several thousand nodes | not applicable |
| Accessible, testable | DOM: focus, labels, Playwright and component tests see it | canvas: nothing to focus or select | not applicable |

The crossing counts and times are of a random graph and say how the three compare, not what a real project's graph will look like. The layout times are measured in Node; a browser is slower on the first run.

## Decided

**React Flow, laid out by dagre; ELK is kept as an option, loaded only if it is needed.**

* A lineage node is a small document (a table with its kind, its connection, whether its upstream is known, its last run, columns that open), which a component draws and a canvas shape does not. The console is React, so React Flow's nodes are the console's own components, in its theme, with its tests.
* dagre is 17 KB and as good as the others on the crossings here; ELK is 5× as large and 8% better. That does not pay for a default. ELK earns its size for ports and compound nodes (a dataset drawn as a box that contains its columns, with edges to the columns), which a later level of detail may want; it is then loaded on demand with `import()`, not in the first page.
* Cytoscape is the right tool at several thousand nodes on one canvas. The view does not draw that many: it draws a bounded slice (the impact or provenance of a node, a depth at a time) and starts at the table level.

## The component (`@dbdatakit/visuals`)

* `projectLineage(graph, { detail, expanded })` turns a joined graph (the console's `GET /api/lineage`, `LineageJson`) into what is drawn. **Table level** (the default): datasets, semantic members and the table edges, and a column edge is folded into the table edge between its datasets. **Column level** for a dataset in `expanded`: its columns appear as rows of its card, and the column edges that touch them are drawn to those rows. A dataset that is not expanded stays one card whatever the rest shows.
* `layoutLineage(nodes, edges)` places them with dagre, left to right: an edge always points right, nodes do not overlap.
* `LineageGraphView` draws the result with React Flow. The run overlay and highlighting the impact of a node are G10.
