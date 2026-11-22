#!/usr/bin/env python3
"""What a build or a test suite reads from Markdown and documentation, checked without a build.

A change to planning, documentation or Markdown alone starts no product suite (scripts/ci_paths.py), yet some of those files are read by a build or
a test. This check reads the same files the way they do, so such a change is still checked, in Repository checks, in seconds:

  module docs     every docs folder a web package registers (`"dbdatakit": {"docs": {...}}` in Shared/web/*/package.json, which the console's build
                  turns into /docs/<id>/, Shared/web/console/build/moduleDocs.mjs): the rules of that build (ids, order.txt against the pages, titles,
                  pictures under ../screenshots/ that exist) and, as the viewer renders them, every link to another page names a page that ships and
                  every #anchor names a heading of its page;
  sync install    DbDataSync/docs/install.md names the default install folder of `dbdatasync tool install` (CliOptions.DefaultToolDir) as Linux and
                  Windows spell it (DbDataSync's InstallDocsTests);
  engine support  docs/engine-support.md lists a section for every engine of its table, and each product README's generated section (between its
                  engine-support markers) says the same as that table (EngineSupportTableTests regenerates both from the engine registry);
  build docs      DbDataBuild/docs/commands.md: its index and its sections agree; the agent kit's SKILL.md (embedded in dbdatabuild): its front matter,
                  that each DDB code, schema and option it names exists, its example model block, and the sentences AgentKitTests looks for;
                  DbDataBuild/DESIGN.md: each marker DesignDocExampleTests reads is there with a yaml block after it;
  packed files    every Markdown file a project file includes or packs (README.md as a package's readme, an embedded resource) and every one a
                  release package script copies is there.

What it cannot see (a generated file that no longer matches its generator, an example a loader would refuse) the product suite finds on the next
change to that product, and a release builds everything. Design: docs/ci-lessons.md#which-changes-start-which-workflow

Run from anywhere: python3 -I scripts/check_doc_inputs.py [root]
"""
import glob
import json
import os
import re
import sys

PICTURE = re.compile(r"!\[[^\]]*\]\((\.\./screenshots/[a-z0-9-]+/[A-Za-z0-9._-]+\.(?:png|jpe?g|gif|webp))\)", re.I)
ANY_PICTURE = re.compile(r"!\[[^\]]*\]\(([^)\s]+)\)")
LINK = re.compile(r"(?<!!)\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)")
DOC_LINK = re.compile(r"^(?:\./)?([a-z0-9-]+)\.md(?:#([A-Za-z0-9_-]*))?$", re.I)
ID = re.compile(r"^[a-z][a-z0-9-]*$")


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read().replace("\r\n", "\n")


def rel(root, path):
    return os.path.relpath(path, root).replace(os.sep, "/")


# ---- module docs -------------------------------------------------------------------------------------------------------------------------------

def read_order(text):
    """The pages of an ordering file, as moduleDocs.mjs reads them: `slug: Title` a line, blank lines and `#` comments skipped."""
    pages = []
    for line in text.split("\n"):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        slug, colon, title = line.partition(":")
        pages.append((slug.strip(), title.strip() if colon else slug.strip()))
    return pages


def strip_code(text):
    """The text without fenced code blocks and inline code, where a link or a heading is not one."""
    out, fence = [], False
    for line in text.split("\n"):
        if line.lstrip().startswith("```"):
            fence = not fence
            out.append("")
            continue
        out.append("" if fence else re.sub(r"`[^`]*`", "", line))
    return "\n".join(out)


def heading_slugs(text):
    """The ids rehype-slug gives the headings of a page (github-slugger): lower case, punctuation dropped, spaces to hyphens, repeats numbered."""
    seen, slugs = {}, set()
    fence = False
    for line in text.split("\n"):
        if line.lstrip().startswith("```"):
            fence = not fence
            continue
        m = None if fence else re.match(r"^#{1,6}\s+(.*?)\s*#*\s*$", line)
        if not m:
            continue
        heading = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", m.group(1))          # a link or a picture: its text
        heading = re.sub(r"<[^>]+>", "", heading).replace("`", "")
        heading = re.sub(r"(\*\*|__|\*|~~)", "", heading)
        slug = re.sub(r"[^\w\- ]", "", heading.lower(), flags=re.UNICODE).replace(" ", "-")
        n = seen.get(slug, 0)
        seen[slug] = n + 1
        slugs.add(slug if n == 0 else f"{slug}-{n}")
    return slugs


def discover_modules(root):
    modules = []
    web = os.path.join(root, "Shared", "web")
    for manifest in sorted(glob.glob(os.path.join(web, "*", "package.json"))):
        if os.sep + "node_modules" + os.sep in manifest:
            continue
        docs = (json.loads(read(manifest)).get("dbdatakit") or {}).get("docs")
        if docs:
            modules.append({"id": docs.get("id", ""), "title": docs.get("title", ""), "order": docs.get("order", "order.txt"),
                            "dir": os.path.normpath(os.path.join(os.path.dirname(manifest), docs.get("dir", ""))), "package": rel(root, manifest)})
    return modules


def check_module(root, module):
    problems = []
    mid, folder = module["id"], module["dir"]
    if not ID.match(mid):
        problems.append(f"{module['package']}: the docs id {mid!r} is not lower-case letters, digits and hyphens")
    if not os.path.isdir(folder):
        return problems + [f"{module['package']}: the docs folder {rel(root, folder)} does not exist"]
    order_file = os.path.join(folder, module["order"])
    where = rel(root, order_file)
    if not os.path.isfile(order_file):
        return problems + [f"{mid}: {where} is missing; it lists the pages in the order of the index, one \"slug: Title\" a line"]
    pages = read_order(read(order_file))
    slugs = [s for s, _ in pages]
    on_disk = sorted(n[:-3] for n in os.listdir(folder) if n.endswith(".md"))
    for slug in sorted({s for s in slugs if slugs.count(s) > 1}):
        problems.append(f"{mid}: {slug} is listed twice in {where}")
    for slug in on_disk:
        if slug not in slugs:
            problems.append(f"{mid}: {rel(root, os.path.join(folder, slug + '.md'))} is not listed in {where}, so nobody could reach it")
    for slug, title in pages:
        if slug not in on_disk:
            problems.append(f"{mid}: {where} lists {slug}, which has no {slug}.md")
        if not title:
            problems.append(f"{mid}: {slug} has no title in {where}")
    shipped = [s for s in slugs if s in on_disk]
    anchors = {s: heading_slugs(read(os.path.join(folder, s + ".md"))) for s in shipped}
    for slug in shipped:
        path = os.path.join(folder, slug + ".md")
        name = rel(root, path)
        text = strip_code(read(path))
        for m in PICTURE.finditer(text):
            if not os.path.isfile(os.path.normpath(os.path.join(folder, m.group(1)))):
                problems.append(f"{name}: shows {m.group(1)}, which is not there")
        for m in ANY_PICTURE.finditer(text):
            if not m.group(1).startswith("../screenshots/") and not re.match(r"^https?:", m.group(1), re.I):
                problems.append(f"{name}: shows {m.group(1)}, which is not a picture under ../screenshots/")
        for m in LINK.finditer(text):
            href = m.group(1)
            if href.startswith("#"):
                if href[1:] not in anchors[slug]:
                    problems.append(f"{name}: the link {href} names no heading of the page")
                continue
            doc = DOC_LINK.match(href)
            if not doc:
                continue
            target = doc.group(1).lower()
            if target not in anchors:
                problems.append(f"{name}: the link {href} names a page that does not ship in {mid}'s docs ({where}), so the console shows it as text")
            elif doc.group(2) and doc.group(2) not in anchors[target]:
                problems.append(f"{name}: the link {href} names no heading of {target}.md")
    return problems


def check_module_docs(root):
    modules = discover_modules(root)
    problems = []
    ids = [m["id"] for m in modules]
    for mid in sorted({i for i in ids if ids.count(i) > 1}):
        problems.append(f"the docs id {mid} is registered by more than one package")
    for module in modules:
        problems += check_module(root, module)
    return problems


# ---- DbDataSync's install page ------------------------------------------------------------------------------------------------------------------

def check_sync_install(root):
    source = os.path.join(root, "DbDataSync", "src", "DbDataSync.Cli", "CliOptions.cs")
    doc = os.path.join(root, "DbDataSync", "docs", "install.md")
    if not os.path.isfile(source):
        return []
    m = re.search(r"DefaultToolDir\s*=>(.*?);", read(source), re.S)
    if not m:
        return [f"{rel(root, source)}: DefaultToolDir was not found, so {rel(root, doc)} cannot be checked against it"]
    literals = re.findall(r'"([^"]*)"', m.group(1))
    windows = re.search(r'SpecialFolder\.ProgramFiles\)\s*,\s*"([^"]+)"', m.group(1))
    expected = []
    if literals:
        expected.append(literals[-1])                                             # the last branch: Linux
    if windows:
        expected.append("$env:ProgramFiles\\" + windows.group(1))
    if not os.path.isfile(doc):
        return [f"{rel(root, doc)} is missing"]
    text = read(doc)
    return [f"{rel(root, doc)} does not name {e}, the default folder of `dbdatasync tool install` ({rel(root, source)})" for e in expected if e not in text]


# ---- engine support ------------------------------------------------------------------------------------------------------------------------------

README_SECTIONS = {"sync": "DbDataSync/README.md", "gateway": "DbDataGateway/README.md", "build": "DbDataBuild/README.md"}


def table_rows(lines):
    """The rows of the first Markdown table in these lines, each a list of cells, the header first and the rule left out."""
    rows = []
    for line in lines:
        if line.startswith("|"):
            cells = [c.strip() for c in line.strip().strip("|").split("|")]
            if not all(re.fullmatch(r":?-+:?", c) for c in cells):
                rows.append(cells)
        elif rows:
            break
    return rows


def check_engine_support(root):
    path = os.path.join(root, "docs", "engine-support.md")
    if not os.path.isfile(path):
        return []
    text = read(path)
    lines = text.split("\n")
    problems = []
    try:
        start = lines.index("## By role")
    except ValueError:
        return [f"docs/engine-support.md has no \"## By role\" table"]
    by_role = table_rows(lines[start + 1:])
    if not by_role:
        return ["docs/engine-support.md: the \"## By role\" table is empty"]
    header, rows = by_role[0], {r[0]: r for r in by_role[1:]}
    sections = set(re.findall(r"^### (.+)$", text, re.M))
    for engine in rows:
        if engine not in sections:
            problems.append(f"docs/engine-support.md: {engine} is in the table but has no \"### {engine}\" section")
    for engine in sorted(sections - set(rows)):
        problems.append(f"docs/engine-support.md: the section \"### {engine}\" has no row in the table")
    for product, readme in README_SECTIONS.items():
        rpath = os.path.join(root, readme)
        if not os.path.isfile(rpath):
            continue
        rtext = read(rpath)
        begin, end = f"<!-- engine-support:begin {product} -->", "<!-- engine-support:end -->"
        if begin not in rtext:
            problems.append(f"{readme}: the line {begin} is missing (the generated engine support section goes between it and {end})")
            continue
        section = rtext[rtext.index(begin):]
        if end not in section:
            problems.append(f"{readme}: the line {end} is missing")
            continue
        section = section[:section.index(end)]
        if "../docs/engine-support.md" not in section:
            problems.append(f"{readme}: the engine support section does not link to ../docs/engine-support.md")
        table = table_rows(section.split("\n"))
        if not table:
            problems.append(f"{readme}: the engine support section has no table")
            continue
        for column in table[0][1:]:
            if column not in header:
                problems.append(f"{readme}: the engine support column {column!r} is not a column of docs/engine-support.md")
        for row in table[1:]:
            engine = row[0]
            if engine not in rows:
                problems.append(f"{readme}: {engine} is not in docs/engine-support.md")
                continue
            for column, value in zip(table[0][1:], row[1:]):
                if column in header and rows[engine][header.index(column)] != value:
                    problems.append(f"{readme}: {engine} / {column} is {value!r}, docs/engine-support.md says {rows[engine][header.index(column)]!r}")
    return problems


# ---- DbDataBuild's documents ---------------------------------------------------------------------------------------------------------------------

def check_command_reference(root):
    path = os.path.join(root, "DbDataBuild", "docs", "commands.md")
    if not os.path.isfile(path):
        return []
    text = read(path)
    listed = re.findall(r"^\| \[`([^`]+)`\]\(#([a-z0-9-]+)\)", text, re.M)
    sections = re.findall(r"^## (.+)$", text, re.M)
    problems = []
    for name, anchor in listed:
        if name not in sections:
            problems.append(f"DbDataBuild/docs/commands.md: {name} is in the index but has no \"## {name}\" section")
        if anchor != name.replace(" ", "-"):
            problems.append(f"DbDataBuild/docs/commands.md: the index links {name} to #{anchor}")
    for name in sections:
        if name not in [n for n, _ in listed]:
            problems.append(f"DbDataBuild/docs/commands.md: the section \"## {name}\" is not in the index")
    return problems


def build_options(root):
    """Every `--option` the command reference names, the options a command takes."""
    path = os.path.join(root, "DbDataBuild", "docs", "commands.md")
    return set(re.findall(r"(?<![\w-])(--[a-z][a-z-]*)", read(path))) if os.path.isfile(path) else set()


def cs_files(*folders):
    for folder in folders:
        yield from glob.glob(os.path.join(folder, "**", "*.cs"), recursive=True)


def check_agent_kit(root):
    build = os.path.join(root, "DbDataBuild")
    path = os.path.join(build, "src", "DbDataBuild.Cli", "AgentKit", "SKILL.md")
    tests = os.path.join(build, "tests", "DbDataBuild.Tests.Unit", "AgentKitTests.cs")
    if not os.path.isfile(path):
        return []
    name = rel(root, path)
    skill = read(path)
    problems = []
    lines = skill.split("\n")
    if len(lines) < 4 or lines[0] != "---" or lines[1] != "name: dbdatabuild" or not lines[2].startswith("description: ") or lines[3] != "---":
        problems.append(f"{name}: the front matter is not the four lines an agent reads (---, name: dbdatabuild, description: ..., ---)")
    elif not 100 <= len(lines[2]) <= 1024:
        problems.append(f"{name}: the description line is {len(lines[2])} characters; it is 100 to 1024")
    codes = set()
    for cs in cs_files(os.path.join(build, "src")):
        codes.update("DDB-" + c for c in re.findall(r'\b[EWN]\(\s*"(\d{3})"', read(cs)))
    if codes:
        for code in sorted(set(re.findall(r"DDB-\d{3}", skill)) - codes):
            problems.append(f"{name}: names {code}, which is not a diagnostic code")
    for schema in sorted(set(re.findall(r"schemas/([a-z.]+\.json)", skill))):
        if not os.path.isfile(os.path.join(build, "schemas", schema)):
            problems.append(f"{name}: names schemas/{schema}, which is not in DbDataBuild/schemas/")
    options = build_options(root)
    test_text = read(tests) if os.path.isfile(tests) else ""
    config_only = re.search(r"ConfigOnly\s*=\s*\[([^\]]*)\]", test_text)
    options.update(re.findall(r'"(--[a-z-]+)"', config_only.group(1)) if config_only else [])
    options.add("--format")
    if len(options) > 1:
        for option in sorted(set(re.findall(r"(?<![\w-])(--[a-z][a-z-]*)", skill)) - options):
            problems.append(f"{name}: names {option}, which is not an option of any command (DbDataBuild/docs/commands.md)")
    example = re.search(r"```yaml\n(.*?)```", skill, re.S)
    if not example:
        problems.append(f"{name}: has no ```yaml example model")
    elif "name: marts.fct_orders" not in example.group(1):
        problems.append(f"{name}: the example model is not marts.fct_orders, which AgentKitTests loads")
    for phrase in re.findall(r'Assert\.Contains\("((?:[^"\\]|\\.)*)",\s*Skill\)', test_text):
        if phrase.replace('\\"', '"') not in skill:
            problems.append(f"{name}: does not say {phrase!r}, which AgentKitTests looks for")
    screens = re.search(r"new\[\]\s*\{([^}]*)\}\)\s*Assert\.Contains\(\$\"`\{screen\}`\",\s*Skill\)", test_text)
    for screen in re.findall(r'"([^"]+)"', screens.group(1)) if screens else []:
        if f"`{screen}`" not in skill:
            problems.append(f"{name}: does not name the screen `{screen}`, which AgentKitTests looks for")
    return problems


def check_design_examples(root):
    design = os.path.join(root, "DbDataBuild", "DESIGN.md")
    if not os.path.isfile(design):
        return []
    text = read(design)
    problems = []
    for cs in cs_files(os.path.join(root, "DbDataBuild", "tests")):
        for marker in re.findall(r'YamlBlockAfter\("((?:[^"\\]|\\.)*)"\)', read(cs)):
            at = text.find(marker)
            if at < 0:
                problems.append(f"DbDataBuild/DESIGN.md: {marker!r} is not there, and {rel(root, cs)} reads the yaml example after it")
            elif "```yaml\n" not in text[at:]:
                problems.append(f"DbDataBuild/DESIGN.md: no ```yaml block follows {marker!r} ({rel(root, cs)} reads it)")
    return problems


# ---- Markdown a build packs or copies -----------------------------------------------------------------------------------------------------------

def check_packed(root):
    problems = []
    for project in sorted(glob.glob(os.path.join(root, "**", "*.csproj"), recursive=True)):
        if any(part in ("node_modules", "bin", "obj") for part in project.split(os.sep)):
            continue
        text = read(project)
        folder = os.path.dirname(project)
        names = set(re.findall(r'Include="([^"*;$]+\.md)"', text)) | set(re.findall(r"<PackageReadmeFile>([^<]+\.md)</PackageReadmeFile>", text))
        for name in sorted(names):
            if not os.path.isfile(os.path.normpath(os.path.join(folder, name.replace("\\", os.sep)))):
                problems.append(f"{rel(root, project)}: includes {name}, which is not there")
    for script in sorted(glob.glob(os.path.join(root, "*", "scripts", "package.sh"))):
        product = os.path.dirname(os.path.dirname(script))
        for line in read(script).split("\n"):
            if re.match(r"\s*cp\b", line):
                for name in re.findall(r"(?<![\w$/])((?:\.\./)?[\w./-]+\.md)\b", line):
                    if not os.path.isfile(os.path.normpath(os.path.join(product, name))):
                        problems.append(f"{rel(root, script)}: copies {name}, which is not there")
    return problems


CHECKS = [check_module_docs, check_sync_install, check_engine_support, check_command_reference, check_agent_kit, check_design_examples, check_packed]


def check(root):
    problems = []
    for one in CHECKS:
        problems += one(root)
    return problems


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    problems = check(root)
    for p in problems:
        print(p)
    if problems:
        print(f"{len(problems)} problem(s) in what a build reads from documentation (scripts/check_doc_inputs.py).")
        return 1
    print("check_doc_inputs: what the builds read from documentation is sound.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
