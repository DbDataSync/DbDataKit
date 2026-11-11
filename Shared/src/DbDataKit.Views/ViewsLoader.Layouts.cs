using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Views;

public static partial class ViewsLoader
{
    private static readonly Regex Token = new(@"\{(?<token>[^{}]*)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const double Epsilon = 1e-9;

    private sealed partial class Reader
    {
        private HashSet<string>? widgetIds;
        private readonly Dictionary<string, (YamlNode Node, string Path)> layoutNodes = [];

        private List<ViewLayout>? ReadLayouts(YamlNode node, List<ViewWidget>? widgets)
        {
            widgetIds = widgets?.Select(w => w.Id).ToHashSet();
            if (Seq(node, "layouts", "`layouts`", node, 1, MaximumLayouts) is not { } list) return null;
            var layouts = new List<ViewLayout>();
            var names = new HashSet<string>();
            for (var i = 0; i < list.Items.Count; i++)
            {
                if (ReadLayout(list.Items[i], I("layouts", i), widgets) is not { } layout) continue;
                var nameNode = ((YamlMapping)list.Items[i]).Get("name")!;
                if (!names.Add(layout.Name)) { Bad(ViewsCodes.DuplicateName, nameNode, J(I("layouts", i), "name"), $"Two layouts are called `{layout.Name}`; a layout's name is unique (a link names it: `?layout={layout.Name}`)."); continue; }
                layoutNodes[layout.Name] = (list.Items[i], I("layouts", i));
                layouts.Add(layout);
            }
            return layouts;
        }

        private ViewLayout? ReadLayout(YamlNode node, string path, List<ViewWidget>? widgets)
        {
            if (Map(node, path, "A layout", node) is not { } map) return null;
            var typeNode = Required(map, "type", path);
            var type = typeNode is null ? null : Choice(typeNode, J(path, "type"), "A layout's `type`", Choices["layoutType"]);
            var name = Required(map, "name", path) is { } nn ? Name(nn, J(path, "name"), "A layout's `name`") : null;
            if (type is null) return null;

            var allowed = Keys["layout.common"].Concat(Keys["layout." + type]).ToArray();
            CheckKeysAt(map, allowed, $"a {type} layout", path, e =>
            {
                var owner = new[] { "reflow", "canvas", "flow" }.FirstOrDefault(t => t != type && Keys["layout." + t].Contains(e.Key.Value));
                if (owner is null) return false;
                Bad(ViewsCodes.KeyOfOtherLayoutType, e.Key, J(path, e.Key.Value), $"`{e.Key.Value}` belongs to a {owner} layout; this layout is a {type} layout.", $"Keys of a {type} layout: {string.Join(", ", allowed)}.");
                return true;
            });
            var isDefault = map.Get("default") is { } d && Flag(d, J(path, "default"), "`default`") == true;
            var when = map.Get("when") is { } w ? ReadWhen(w, path) : null;
            if (map.Get("widget_options") is { } wo) ReadOptions(wo, J(path, "widget_options"));

            var placed = new List<string>();
            ReflowGrid? grid = null; CanvasSpec? canvas = null; var tiles = new List<ViewTile>();
            switch (type)
            {
                case "reflow": grid = ReadGrid(map, path); ReadTiles(map, path, false, tiles, placed, widgets, null, grid); break;
                case "canvas": canvas = ReadCanvas(map, path); ReadTiles(map, path, true, tiles, placed, widgets, canvas, null); break;
                default: ReadFlow(map, path, placed); break;
            }
            if (name is null) return null;
            var layoutType = type switch { "reflow" => LayoutType.Reflow, "canvas" => LayoutType.Canvas, _ => LayoutType.Flow };
            return new ViewLayout(name, layoutType, isDefault, when, placed, grid, canvas, tiles, ToObject(map), map.Line, map.Column);
        }

        // ---- selector and options ----

        private LayoutWhen? ReadWhen(YamlNode node, string layoutPath)
        {
            var path = J(layoutPath, "when");
            if (Map(node, path, "`when`", node) is not { } m) return null;
            CheckKeysAt(m, Keys["when"], "a selector", path);
            ViewRange? width = null, height = null, aspect = null; string? orientation = null; List<string>? medium = null;
            if (m.Get("width") is { } wn) width = Range(wn, J(path, "width"));
            if (m.Get("height") is { } hn) height = Range(hn, J(path, "height"));
            if (m.Get("aspect") is { } an) aspect = Range(an, J(path, "aspect"));
            if (m.Get("orientation") is { } on) orientation = Choice(on, J(path, "orientation"), "`orientation`", Choices["orientation"], ViewsCodes.BadSelector);
            if (m.Get("medium") is { } mn)
            {
                if (mn is YamlSequence ms)
                {
                    medium = [];
                    if (ms.Items.Count == 0) Bad(ViewsCodes.BadSelector, ms, J(path, "medium"), "`medium` lists at least one of screen, print, export.");
                    for (var i = 0; i < ms.Items.Count; i++)
                        if (Choice(ms.Items[i], I(J(path, "medium"), i), "A medium", Choices["medium"], ViewsCodes.BadSelector) is { } v)
                        {
                            if (medium.Contains(v)) Bad(ViewsCodes.BadSelector, ms.Items[i], I(J(path, "medium"), i), $"`{v}` is listed twice.");
                            else medium.Add(v);
                        }
                }
                else if (Choice(mn, J(path, "medium"), "`medium`", Choices["medium"], ViewsCodes.BadSelector) is { } single) medium = [single];
            }
            return new LayoutWhen(width, height, orientation, aspect, medium);
        }

        private ViewRange? Range(YamlNode node, string path)
        {
            if (node is not YamlMapping m || m.Entries.Count == 0) { Bad(ViewsCodes.BadSelector, node, path, "A range has `min`, `max` or both."); return null; }
            CheckKeysAt(m, Keys["range"], "a range", path);
            double? min = null, max = null;
            if (m.Get("min") is { } mn) min = Number(mn, J(path, "min"), "`min`", min: 0, code: ViewsCodes.BadSelector);
            if (m.Get("max") is { } mx) max = Number(mx, J(path, "max"), "`max`", min: 0, code: ViewsCodes.BadSelector);
            if (min is { } lo && max is { } hi && lo > hi) Warn(ViewsCodes.EmptyRange, m, path, $"The range has its minimum {lo} above its maximum {hi}, so it matches nothing.");
            return new ViewRange(min, max);
        }

        private void ReadOptions(YamlNode node, string path)
        {
            if (Map(node, path, "`widget_options`", node) is not { } m) return;
            foreach (var e in m.Entries)
            {
                var wp = J(path, e.Key.Value);
                if (widgetIds is not null && !widgetIds.Contains(e.Key.Value)) { Bad(ViewsCodes.UnknownWidget, e.Key, wp, $"`{e.Key.Value}` is not a widget of this document."); continue; }
                if (Map(e.Value, wp, "A widget's options", m) is not { } o) continue;
                CheckKeysAt(o, Keys["widget_options"], "a layout's options for a widget", wp, k =>
                {
                    if (!WidgetOwnKeys.Contains(k.Key.Value)) return false;
                    Bad(ViewsCodes.OptionChangesWidget, k.Key, J(wp, k.Key.Value), $"A layout restyles a widget and never changes it: `{k.Key.Value}` is part of the widget, so every layout would show different numbers.", $"A layout sets only: {string.Join(", ", Keys["widget_options"])}.");
                    return true;
                });
                if (o.Get("density") is { } d) Choice(d, J(wp, "density"), "`density`", Choices["density"]);
                if (o.Get("show_title") is { } st) Flag(st, J(wp, "show_title"), "`show_title`");
                if (o.Get("legend") is { } lg) Choice(lg, J(wp, "legend"), "`legend`", Choices["legend"]);
            }
        }

        // ---- reflow and canvas ----

        private ReflowGrid? ReadGrid(YamlMapping layout, string path)
        {
            var gp = J(path, "grid");
            if (Required(layout, "grid", path) is not { } g || Map(g, gp, "`grid`", layout) is not { } gm) return null;
            CheckKeysAt(gm, Keys["grid"], "`grid`", gp);
            var columns = Required(gm, "columns", gp) is { } c ? Whole(c, J(gp, "columns"), "`columns`", 1, 48) : null;
            int? rows = gm.Get("rows") is { } r ? Whole(r, J(gp, "rows"), "`rows`", 1, 100) : null;
            return columns is null ? null : new ReflowGrid(columns.Value, rows);
        }

        private CanvasSpec? ReadCanvas(YamlMapping layout, string path)
        {
            var cp = J(path, "canvas");
            CanvasSpec? spec = null;
            if (Required(layout, "canvas", path) is { } c && Map(c, cp, "`canvas`", layout) is { } cm)
            {
                CheckKeysAt(cm, Keys["canvas"], "`canvas`", cp);
                var width = Required(cm, "width", cp) is { } w ? Number(w, J(cp, "width"), "`width`", positive: true, code: ViewsCodes.BadCanvas) : null;
                var height = Required(cm, "height", cp) is { } h ? Number(h, J(cp, "height"), "`height`", positive: true, code: ViewsCodes.BadCanvas) : null;
                var unit = cm.Get("unit") is { } u ? Choice(u, J(cp, "unit"), "`unit`", Choices["unit"], ViewsCodes.BadCanvas) : "px";
                var overflow = layout.Get("overflow") is { } o ? Choice(o, J(path, "overflow"), "`overflow`", Choices["overflow"], ViewsCodes.BadCanvas) : "scale";
                double? min = layout.Get("min_scale") is { } mn ? Number(mn, J(path, "min_scale"), "`min_scale`", positive: true, code: ViewsCodes.BadCanvas) : null;
                double? max = layout.Get("max_scale") is { } mx ? Number(mx, J(path, "max_scale"), "`max_scale`", positive: true, code: ViewsCodes.BadCanvas) : null;
                if (width is { } wv && height is { } hv && unit is not null && overflow is not null)
                {
                    spec = new CanvasSpec(wv, hv, unit, overflow, min, max);
                    foreach (var (side, value, node) in new[] { ("width", spec.WidthPx, cm.Get("width")!), ("height", spec.HeightPx, cm.Get("height")!) })
                        if (value < MinimumCanvasPixels - Epsilon || value > MaximumCanvasPixels + Epsilon)
                            Bad(ViewsCodes.BadCanvas, node, J(cp, side), $"A canvas is {MinimumCanvasPixels} to {MaximumCanvasPixels} px on each side (96 px per inch); the {side} here is {value:0.##} px.");
                    if (overflow == "scroll" && (layout.Get("min_scale") is not null || layout.Get("max_scale") is not null))
                        Bad(ViewsCodes.BadCanvas, (layout.Get("min_scale") ?? layout.Get("max_scale"))!, J(path, layout.Get("min_scale") is not null ? "min_scale" : "max_scale"), "A canvas with `overflow: scroll` is shown at scale 1: `min_scale` and `max_scale` are for `overflow: scale`.");
                    if ((min ?? DefaultMinScale) > (max ?? DefaultMaxScale))
                        Bad(ViewsCodes.BadCanvas, (layout.Get("min_scale") ?? layout.Get("max_scale") ?? layout)!, J(path, layout.Get("min_scale") is not null ? "min_scale" : "max_scale"), $"`min_scale` ({min ?? DefaultMinScale}) is above `max_scale` ({max ?? DefaultMaxScale}).");
                }
            }
            return spec;
        }

        private void ReadTiles(YamlMapping layout, string path, bool canvasTiles, List<ViewTile> tiles, List<string> placed, List<ViewWidget>? widgets, CanvasSpec? canvas, ReflowGrid? grid)
        {
            var tp = J(path, "tiles");
            if (Required(layout, "tiles", path) is not { } tn || Seq(tn, tp, "`tiles`", layout, 1, MaximumTiles) is not { } list) return;
            var nodes = new List<YamlMapping>();
            for (var i = 0; i < list.Items.Count; i++)
            {
                var ip = I(tp, i);
                if (Map(list.Items[i], ip, "A tile", list) is not { } m) continue;
                CheckKeysAt(m, Keys[canvasTiles ? "tile.canvas" : "tile.reflow"], $"a tile of a {(canvasTiles ? "canvas" : "reflow")} layout", ip, e =>
                {
                    if (canvasTiles || e.Key.Value != "layer") return false;
                    Bad(ViewsCodes.KeyOfOtherLayoutType, e.Key, J(ip, "layer"), "`layer` belongs to a canvas layout; a reflow layout has no overlapping tiles.");
                    return true;
                });
                var widget = Required(m, "widget", ip) is { } wn ? Name(wn, J(ip, "widget"), "A tile's `widget`") : null;
                double? x = null, y = null, w = null, h = null;
                if (canvasTiles)
                {
                    x = Required(m, "x", ip) is { } xn ? Number(xn, J(ip, "x"), "`x`", min: 0) : null;
                    y = Required(m, "y", ip) is { } yn ? Number(yn, J(ip, "y"), "`y`", min: 0) : null;
                    w = Required(m, "w", ip) is { } wd ? Number(wd, J(ip, "w"), "`w`", positive: true) : null;
                    h = Required(m, "h", ip) is { } hd ? Number(hd, J(ip, "h"), "`h`", positive: true) : null;
                }
                else
                {
                    x = Required(m, "x", ip) is { } xn ? Whole(xn, J(ip, "x"), "`x`", 0, 10_000) : null;
                    y = Required(m, "y", ip) is { } yn ? Whole(yn, J(ip, "y"), "`y`", 0, 10_000) : null;
                    w = Required(m, "w", ip) is { } wd ? Whole(wd, J(ip, "w"), "`w`", 1, 10_000) : null;
                    h = Required(m, "h", ip) is { } hd ? Whole(hd, J(ip, "h"), "`h`", 1, 10_000) : null;
                }
                var layer = m.Get("layer") is { } ln ? Whole(ln, J(ip, "layer"), "`layer`", 0, 20) ?? 0 : 0;
                double? minW = null, minH = null;
                if (m.Get("min") is { } mn && Map(mn, J(ip, "min"), "`min`", m) is { } mm)
                {
                    CheckKeysAt(mm, Keys["min"], "`min`", J(ip, "min"));
                    if (mm.Get("w") is { } a) minW = Number(a, J(J(ip, "min"), "w"), "`w`", positive: true);
                    if (mm.Get("h") is { } b) minH = Number(b, J(J(ip, "min"), "h"), "`h`", positive: true);
                }
                if (widget is null || x is null || y is null || w is null || h is null) continue;
                if (widgetIds is not null && !widgetIds.Contains(widget)) { Bad(ViewsCodes.UnknownWidget, m.Get("widget")!, J(ip, "widget"), $"The layout places `{widget}`, which is not a widget of this document."); continue; }
                if (placed.Contains(widget)) { Bad(ViewsCodes.DuplicateName, m.Get("widget")!, J(ip, "widget"), $"The layout places `{widget}` twice."); continue; }
                placed.Add(widget);
                tiles.Add(new ViewTile(widget, x.Value, y.Value, w.Value, h.Value, layer, minW, minH, m.Line));
                nodes.Add(m);
            }
            CheckGeometry(tiles, nodes, tp, canvasTiles, canvas, grid, widgets);
        }

        private void CheckGeometry(List<ViewTile> tiles, List<YamlMapping> nodes, string tp, bool canvasTiles, CanvasSpec? canvas, ReflowGrid? grid, List<ViewWidget>? widgets)
        {
            if (!canvasTiles && grid is null || canvasTiles && canvas is null) return;
            for (var i = 0; i < tiles.Count; i++)
            {
                var t = tiles[i];
                var ip = I(tp, i);
                if (!canvasTiles)
                {
                    if (t.X + t.W > grid!.Columns) Bad(ViewsCodes.TileOutside, nodes[i], ip, $"The tile for `{t.Widget}` ends at column {t.X + t.W}; the grid has {grid.Columns} columns.");
                    else if (grid.Rows is { } rows && t.Y + t.H > rows) Bad(ViewsCodes.TileOutside, nodes[i], ip, $"The tile for `{t.Widget}` ends at row {t.Y + t.H}; the grid has {rows} rows.");
                }
                else
                {
                    if (t.X + t.W > canvas!.Width + Epsilon || t.Y + t.H > canvas.Height + Epsilon)
                        Bad(ViewsCodes.TileOutside, nodes[i], ip, $"The tile for `{t.Widget}` reaches ({t.X + t.W:0.##}, {t.Y + t.H:0.##}) and the canvas is {canvas.Width:0.##} by {canvas.Height:0.##} {canvas.Unit}.");
                    var type = widgets?.FirstOrDefault(w => w.Id == t.Widget)?.Type;
                    if (type is not null && Minimums.TryGetValue(type, out var min))
                    {
                        var needW = Math.Max(min.W, t.MinW ?? 0); var needH = Math.Max(min.H, t.MinH ?? 0);
                        var haveW = t.W * canvas.PixelsPerUnit; var haveH = t.H * canvas.PixelsPerUnit;
                        if (haveW < needW - Epsilon || haveH < needH - Epsilon)
                            Bad(ViewsCodes.TileBelowMinimum, nodes[i], ip, $"The {type} `{t.Widget}` is {haveW:0.##} by {haveH:0.##} px at scale 1; a {type} needs at least {needW:0.##} by {needH:0.##}.", "Make the tile larger; a canvas is shown at its own size, so the minimums apply as they are.");
                    }
                }
                for (var j = 0; j < i; j++)
                {
                    var o = tiles[j];
                    if (canvasTiles && o.Layer != t.Layer) continue;
                    if (t.X < o.X + o.W - Epsilon && o.X < t.X + t.W - Epsilon && t.Y < o.Y + o.H - Epsilon && o.Y < t.Y + t.H - Epsilon)
                        Bad(ViewsCodes.TilesOverlap, nodes[i], ip, $"The tiles for `{t.Widget}` and `{o.Widget}` overlap" + (canvasTiles ? " on layer " + t.Layer + "; give one a different `layer`." : "."));
                }
            }
        }

        // ---- flow ----

        private void ReadFlow(YamlMapping layout, string path, List<string> placed)
        {
            if (layout.Get("page") is { } pn && Map(pn, J(path, "page"), "`page`", layout) is { } pm)
            {
                var pp = J(path, "page");
                CheckKeysAt(pm, Keys["page"], "`page`", pp);
                if (pm.Get("size") is { } sz)
                {
                    if (sz is YamlMapping sm)
                    {
                        CheckKeysAt(sm, ["w", "h"], "a page size", J(pp, "size"));
                        foreach (var k in new[] { "w", "h" })
                            if (Required(sm, k, J(pp, "size")) is { } kn) Number(kn, J(J(pp, "size"), k), $"`{k}` (millimetres)", min: 50, max: 2000);
                    }
                    else Choice(sz, J(pp, "size"), "`size`", Choices["pageSize"], ViewsCodes.BadFlow);
                }
                if (pm.Get("orientation") is { } o) Choice(o, J(pp, "orientation"), "`orientation`", Choices["orientation"]);
                if (pm.Get("margins") is { } mg && Map(mg, J(pp, "margins"), "`margins`", pm) is { } mm)
                {
                    CheckKeysAt(mm, Keys["margins"], "`margins`", J(pp, "margins"));
                    foreach (var e in mm.Entries.Where(e => Keys["margins"].Contains(e.Key.Value))) Number(e.Value, J(J(pp, "margins"), e.Key.Value), $"`{e.Key.Value}` (millimetres)", min: 0, max: 100);
                }
            }
            foreach (var part in new[] { "header", "footer" })
                if (layout.Get(part) is { } hn && Map(hn, J(path, part), $"`{part}`", layout) is { } hm)
                {
                    CheckKeysAt(hm, Keys["running_text"], $"`{part}`", J(path, part));
                    foreach (var e in hm.Entries.Where(e => Keys["running_text"].Contains(e.Key.Value))) RunningText(e.Value, J(J(path, part), e.Key.Value));
                }
            if (layout.Get("cover") is { } cn && Map(cn, J(path, "cover"), "`cover`", layout) is { } cm)
            {
                CheckKeysAt(cm, Keys["cover"], "`cover`", J(path, "cover"));
                if (cm.Get("parameters") is { } cp) Flag(cp, J(J(path, "cover"), "parameters"), "`parameters`");
            }
            var sp = J(path, "sections");
            if (Required(layout, "sections", path) is not { } sn || Seq(sn, sp, "`sections`", layout, 1, 200) is not { } sections) return;
            for (var i = 0; i < sections.Items.Count; i++)
            {
                var ip = I(sp, i);
                if (Map(sections.Items[i], ip, "A section", sections) is not { } s) continue;
                CheckKeysAt(s, Keys["section"], "a section", ip);
                if (s.Get("title") is { } t) Text(t, J(ip, "title"), "`title`");
                if (s.Get("break") is { } b) Choice(b, J(ip, "break"), "`break`", Choices["break"]);
                if (Required(s, "blocks", ip) is not { } bn || Seq(bn, J(ip, "blocks"), "`blocks`", s, 1, 200) is not { } blocks) continue;
                for (var j = 0; j < blocks.Items.Count; j++) FlowBlock(blocks.Items[j], I(J(ip, "blocks"), j), placed);
            }
        }

        private void RunningText(YamlNode node, string path)
        {
            if (Text(node, path, "Header and footer text", 200) is not { } text) return;
            foreach (Match m in Token.Matches(text))
            {
                var token = m.Groups["token"].Value;
                if (RunningTokens.Contains(token)) continue;
                if (token.StartsWith("params.", StringComparison.Ordinal))
                {
                    if (!IsDeclared(token[7..])) Bad(ViewsCodes.UndeclaredParameter, node, path, $"`{{{token}}}` names a parameter the document does not declare.", "Declare it under `parameters`.");
                    continue;
                }
                Bad(ViewsCodes.BadFlow, node, path, $"`{{{token}}}` is not a token of header or footer text.", $"Tokens: {string.Join(", ", RunningTokens.Select(t => "{" + t + "}"))} and {{params.name}}.");
            }
        }

        private void FlowBlock(YamlNode node, string path, List<string> placed)
        {
            if (Map(node, path, "A block", node) is not { } b) return;
            var kinds = new[] { "widget", "row", "pagebreak" }.Where(k => b.Get(k) is not null).ToList();
            if (kinds.Count != 1) { Bad(ViewsCodes.InvalidValue, b, path, "A block is exactly one of `widget`, `row` or `pagebreak`."); return; }
            switch (kinds[0])
            {
                case "pagebreak":
                    CheckKeysAt(b, Keys["block.pagebreak"], "a page break", path);
                    if (b.Get("pagebreak") is not YamlScalar { Quoted: false, Value: "true" }) Bad(ViewsCodes.InvalidValue, b.Get("pagebreak")!, J(path, "pagebreak"), "`pagebreak` is `true`.");
                    break;
                case "widget":
                    CheckKeysAt(b, Keys["block.widget"], "a widget block", path);
                    Place(b.Get("widget")!, J(path, "widget"), placed);
                    if (b.Get("height") is { } h) Number(h, J(path, "height"), "`height` (millimetres)", positive: true, max: 2000);
                    if (b.Get("keep") is { } k) Flag(k, J(path, "keep"), "`keep`");
                    if (b.Get("repeat_header") is { } r) Flag(r, J(path, "repeat_header"), "`repeat_header`");
                    break;
                default:
                    CheckKeysAt(b, Keys["block.row"], "a row", path);
                    if (b.Get("keep") is { } rk) Flag(rk, J(path, "keep"), "`keep`");
                    if (Seq(b.Get("row"), J(path, "row"), "`row`", b, 1, 12) is not { } row) break;
                    var total = 0;
                    for (var i = 0; i < row.Items.Count; i++)
                    {
                        var ip = I(J(path, "row"), i);
                        if (Map(row.Items[i], ip, "A row entry", row) is not { } e) continue;
                        CheckKeysAt(e, Keys["row_item"], "a row entry", ip);
                        if (Required(e, "widget", ip) is { } wn) Place(wn, J(ip, "widget"), placed);
                        if (Required(e, "span", ip) is { } sp && Whole(sp, J(ip, "span"), "`span` (of 12 columns)", 1, 12) is { } span) total += span;
                    }
                    if (total > 12) Bad(ViewsCodes.BadFlow, row, J(path, "row"), $"The spans of a row add up to {total}; a row has 12 columns.");
                    break;
            }
        }

        private void Place(YamlNode node, string path, List<string> placed)
        {
            if (Name(node, path, "A block's `widget`") is not { } id) return;
            if (widgetIds is not null && !widgetIds.Contains(id)) { Bad(ViewsCodes.UnknownWidget, node, path, $"The layout places `{id}`, which is not a widget of this document."); return; }
            if (placed.Contains(id)) { Bad(ViewsCodes.DuplicateName, node, path, $"The layout places `{id}` twice."); return; }
            placed.Add(id);
        }

        // ---- the rules across the document ----

        private void CheckDocument(YamlMapping map, List<ViewWidget> widgets, List<ViewLayout> layouts, string? kioskLayout, YamlNode? kioskNode)
        {
            var defaults = layouts.Where(l => l.IsDefault).ToList();
            if (defaults.Count != 1)
            {
                var at = defaults.Count > 1 ? layoutNodes[defaults[1].Name].Node : map.Get("layouts")!;
                var path = defaults.Count > 1 ? layoutNodes[defaults[1].Name].Path : "layouts";
                Bad(ViewsCodes.DefaultLayoutCount, at, path, defaults.Count == 0 ? "No layout says `default: true`; exactly one does, so a full view always exists." : $"{defaults.Count} layouts say `default: true`; exactly one does.");
            }
            else
            {
                var def = defaults[0];
                var (node, path) = layoutNodes[def.Name];
                var allowed = kind == ViewKind.Dashboard ? new[] { LayoutType.Reflow, LayoutType.Canvas } : [LayoutType.Flow];
                if (!allowed.Contains(def.Type))
                    Bad(ViewsCodes.DefaultLayoutType, node, path, kind == ViewKind.Dashboard
                        ? $"The default layout of a dashboard is a fit layout (reflow or canvas); `{def.Name}` is a flow layout."
                        : $"The default layout of a report is a flow layout; `{def.Name}` is a {def.Type.ToString().ToLowerInvariant()} layout.",
                        kind == ViewKind.Dashboard ? "Make a reflow or canvas layout the default; a flow layout can stay as the print layout." : "Make the flow layout the default; a fit layout can stay for the screen.");
                var missing = widgets.Where(w => !def.Placed.Contains(w.Id) && layouts.Any(l => l.Placed.Contains(w.Id))).Select(w => w.Id).ToList();
                if (missing.Count > 0)
                    Bad(ViewsCodes.DefaultMissesWidget, node, path, $"The default layout `{def.Name}` does not place {string.Join(", ", missing.Select(m => $"`{m}`"))}; the default places every widget, so a full view always exists.");
            }
            foreach (var w in widgets.Where(w => layouts.All(l => !l.Placed.Contains(w.Id))))
                Bad(ViewsCodes.DeadWidget, (YamlNode)((YamlMapping)map.Get("widgets")!).GetEntry(w.Id)!.Key, J("widgets", w.Id), $"No layout places the widget `{w.Id}`; it would never be shown.");
            foreach (var p in parameters.Where(p => p.Required && !p.HasDefault))
            {
                var controls = widgets.Where(w => w.Type == "control" && w.Raw["parameter"]?.GetValue<string>() == p.Name).Select(w => w.Id).ToList();
                if (controls.Count == 0) continue;
                foreach (var l in layouts.Where(l => !controls.Any(c => l.Placed.Contains(c))))
                {
                    var (node, path) = layoutNodes[l.Name];
                    Bad(ViewsCodes.ControlOmitted, node, path, $"The layout `{l.Name}` omits the control of the required parameter `{p.Name}` (no default), so a viewer of it could not set the value.", $"Place {string.Join(" or ", controls.Select(c => $"`{c}`"))} in the layout.");
                }
            }
            if (kioskLayout is not null && layouts.All(l => l.Name != kioskLayout))
                Bad(ViewsCodes.InvalidValue, kioskNode!, "kiosk.layout", $"`kiosk.layout` names `{kioskLayout}`; the layouts are {string.Join(", ", layouts.Select(l => $"`{l.Name}`"))}.");
            for (var i = 0; i < layouts.Count; i++)
                for (var j = 0; j < i; j++)
                    if (layouts[i].When is { } a && layouts[j].When is { } b && a.SameAs(b))
                    {
                        var (node, path) = layoutNodes[layouts[i].Name];
                        Warn(ViewsCodes.EqualSelectors, node, path, $"The layouts `{layouts[j].Name}` and `{layouts[i].Name}` have equal selectors; `{layouts[i].Name}` is never reached by selection (it can still be named).");
                        break;
                    }
        }
    }
}
