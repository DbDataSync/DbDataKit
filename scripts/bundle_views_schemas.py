#!/usr/bin/env python3
"""Bundles the schemas of a dashboard and a report document (plan 32 DBR21) into one file each, for editors.

Shared/web/visuals/schemas/{dashboard,report}.schema.json refer to core, vega-lite-subset and view by absolute `$id`; an editor that
reads one file (VS Code's YAML extension, a JSON Schema store, the agent kit) cannot follow those. The bundle copies the `$defs` of every
document the kind reaches into one `$defs`, each under its document's prefix (`core__name`, `view__sort`), and turns every reference
into a local pointer. Written to Shared/web/visuals/schemas/bundled/ and to DbDataBuild/schemas/ (Build's project schemas). The sources stay the authority:

    python3 -I scripts/bundle_views_schemas.py           # writes the bundles
    python3 -I scripts/bundle_views_schemas.py --check   # fails when a bundle is not what the sources give (a unit test runs this)
"""
import json
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
SCHEMAS = os.path.join(ROOT, "Shared", "web", "visuals", "schemas")
OUT = os.path.join(SCHEMAS, "bundled")
# Build's project schemas (the agent kit and the editor settings point at them): the same bundles, so a project's dashboards and reports complete in an editor
BUILD = os.path.join(ROOT, "DbDataBuild", "schemas")
KINDS = ("dashboard", "report")
REF = re.compile(r"^(?P<id>https?://[^#]*)?(?P<frag>#.*)?$")


def read(name):
    with open(os.path.join(SCHEMAS, name + ".schema.json"), encoding="utf-8") as f:
        return json.load(f)


def prefix_of(schema_id):
    return os.path.basename(schema_id).removesuffix(".schema.json").replace("-", "_")


def bundle(kind, loader=read):
    root = loader(kind)
    docs = {root["$id"]: (kind, root)}
    by_id = {}

    def load_by_id(schema_id):
        if schema_id in by_id:
            return by_id[schema_id]
        name = os.path.basename(schema_id).removesuffix(".schema.json")
        doc = loader(name)
        if doc["$id"] != schema_id:
            raise SystemExit(f"{name}: its $id is {doc['$id']}, but a reference names {schema_id}")
        by_id[schema_id] = doc
        return doc

    by_id[root["$id"]] = root
    defs = {}
    pending = [root["$id"]]
    done = set()

    def rewrite(node, own_prefix, own_id):
        if isinstance(node, dict):
            out = {}
            for k, v in node.items():
                if k == "$ref" and isinstance(v, str):
                    m = REF.match(v)
                    target_id = m.group("id") or own_id
                    frag = m.group("frag") or ""
                    if target_id != own_id and target_id not in done and target_id not in pending:
                        pending.append(target_id)
                    target_prefix = prefix_of(target_id)
                    if target_id == root["$id"]:
                        out[k] = frag or "#"
                    elif frag.startswith("#/$defs/"):
                        out[k] = "#/$defs/" + target_prefix + "__" + frag[len("#/$defs/"):]
                    else:
                        raise SystemExit(f"cannot bundle the reference {v}: it must point into $defs")
                elif k in ("$id", "$schema"):
                    continue
                else:
                    out[k] = rewrite(v, own_prefix, own_id)
            return out
        if isinstance(node, list):
            return [rewrite(v, own_prefix, own_id) for v in node]
        return node

    body = None
    while pending:
        schema_id = pending.pop(0)
        if schema_id in done:
            continue
        done.add(schema_id)
        doc = load_by_id(schema_id)
        prefix = prefix_of(schema_id)
        if schema_id == root["$id"]:
            rest = {k: v for k, v in doc.items() if k != "$defs"}
            body = rewrite(rest, prefix, schema_id)
        for name, d in (doc.get("$defs") or {}).items():
            defs[prefix + "__" + name] = rewrite(d, prefix, schema_id)
    result = {"$schema": root["$schema"], "title": root.get("title", kind)}
    result["description"] = (root.get("description", "") + " Bundled from Shared/web/visuals/schemas by scripts/bundle_views_schemas.py: edit the sources, not this file.").strip()
    for k, v in body.items():
        if k not in result:
            result[k] = v
    result["$defs"] = dict(sorted(defs.items()))
    return result


def text_of(kind):
    return json.dumps(bundle(kind), indent=2, ensure_ascii=False) + "\n"


def main(argv):
    check = "--check" in argv
    problems = []
    for kind in KINDS:
        text = text_of(kind)
        for folder in (OUT, BUILD):
            path = os.path.join(folder, kind + ".schema.json")
            if check:
                current = open(path, encoding="utf-8", newline="").read().replace("\r\n", "\n") if os.path.exists(path) else None
                if current != text:
                    problems.append(f"{os.path.relpath(path, ROOT)} is not what the sources give: run python3 -I scripts/bundle_views_schemas.py")
            else:
                os.makedirs(folder, exist_ok=True)
                with open(path, "w", encoding="utf-8", newline="\n") as f:
                    f.write(text)
    for p in problems:
        print(p)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
