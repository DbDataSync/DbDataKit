import json, subprocess, sys, re

SETUP = open('setup.sql').read()
def duck(sql, mode='-list'):
    r = subprocess.run(['duckdb', '-noheader', mode, '-c', SETUP + "\n" + sql], capture_output=True, text=True)
    return (r.stdout + r.stderr).strip()

def plan(sql):
    out = duck(f"SELECT json_serialize_plan($q${sql}$q$, optimize := false, skip_null := true, skip_empty := true);")
    return json.loads(out)['plans'][0]

def q(name): return '"' + name.replace('"', '""') + '"'

def typ(t):
    i = t['id']; ti = t.get('type_info') or {}
    if i == 'DECIMAL': return f"DECIMAL({ti.get('width',18)},{ti.get('scale',3)})"
    return {'VARCHAR': 'VARCHAR', 'HUGEINT': 'HUGEINT'}.get(i, i)

OPS = {'+', '-', '*', '/', '%', '||', '//', '**'}

class Unsupported(Exception): pass

def expr(e, cols):
    t = e['type']
    if t == 'BOUND_REF': return q(cols[e['index']])
    if t == 'VALUE_CONSTANT':
        v = e['value']
        if v.get('is_null'): return 'NULL'
        ty = v['type']['id']; x = v['value']
        if ty == 'DECIMAL':
            sc = (v['type'].get('type_info') or {}).get('scale', 0)
            s = str(abs(x)).rjust(sc + 1, '0')
            return ('-' if x < 0 else '') + (s[:-sc] + '.' + s[-sc:] if sc else s)
        if ty == 'VARCHAR': return "'" + x.replace("'", "''") + "'"
        if ty == 'BOOLEAN': return 'TRUE' if x else 'FALSE'
        return str(x)
    if t == 'OPERATOR_CAST': return f"CAST({expr(e['child'], cols)} AS {typ(e['return_type'])})"
    if t == 'BOUND_FUNCTION':
        a = [expr(c, cols) for c in e.get('children', [])]
        return f"({a[0]} {e['name']} {a[1]})" if e['name'] in OPS and len(a) == 2 else f"{e['name']}({', '.join(a)})"
    if t == 'BOUND_AGGREGATE':
        a = [expr(c, cols) for c in e.get('children', [])]
        s = f"{e['name']}({', '.join(a) if a else '*'})"
        return s + (f" FILTER (WHERE {expr(e['filter'], cols)})" if e.get('filter') else '')
    if t.startswith('COMPARE_'):
        op = {'COMPARE_EQUAL': '=', 'COMPARE_NOTEQUAL': '<>', 'COMPARE_LESSTHAN': '<', 'COMPARE_GREATERTHAN': '>',
              'COMPARE_LESSTHANOREQUALTO': '<=', 'COMPARE_GREATERTHANOREQUALTO': '>=', 'COMPARE_NOT_DISTINCT_FROM': 'IS NOT DISTINCT FROM',
              'COMPARE_DISTINCT_FROM': 'IS DISTINCT FROM'}[t]
        return f"({expr(e['left'], cols)} {op} {expr(e['right'], cols)})"
    raise Unsupported(t)

def node(p):
    """returns (sql, output column names)"""
    t = p['type']
    if t == 'LOGICAL_GET':
        fd = p['function_data']; names = p['names']
        idx = [c['index'] for c in p.get('column_indexes', [])] or list(range(len(names)))
        cols = [names[i] for i in idx]
        return f"SELECT {', '.join(q(c) for c in cols)} FROM {q(fd['schema'])}.{q(fd['table'])}", cols
    if t == 'LOGICAL_PROJECTION':
        sub, cols = node(p['children'][0])
        outs = []
        for i, e in enumerate(p['expressions']):
            outs.append((expr(e, cols), e.get('alias') or f"c{i}"))
        return f"SELECT {', '.join(f'{x} AS {q(a)}' for x, a in outs)} FROM ({sub}) AS s", [a for _, a in outs]
    if t == 'LOGICAL_FILTER':
        sub, cols = node(p['children'][0])
        return f"SELECT * FROM ({sub}) AS s WHERE {' AND '.join(expr(e, cols) for e in p['expressions'])}", cols
    if t == 'LOGICAL_AGGREGATE_AND_GROUP_BY':
        sub, cols = node(p['children'][0])
        gs = [expr(g, cols) for g in p.get('groups', [])]
        ags = [expr(a, cols) for a in p.get('expressions', [])]
        names = [f"g{i}" for i in range(len(gs))] + [f"a{i}" for i in range(len(ags))]
        sel = ', '.join(f"{x} AS {q(n)}" for x, n in zip(gs + ags, names))
        return f"SELECT {sel} FROM ({sub}) AS s" + (f" GROUP BY {', '.join(gs)}" if gs else ''), names
    raise Unsupported(t)

CASES = [
  ("macro (scalar, default + named arg)", "SELECT net(population) AS g, net(population, tax := 0.1) AS h FROM staging.cities"),
  ("explicit PIVOT", "SELECT * FROM staging.cities PIVOT (sum(population) FOR year IN (2000, 2010) GROUP BY country)"),
  ("star EXCLUDE + COLUMNS regex", "SELECT * EXCLUDE (note), COLUMNS('^(j|f)') FROM staging.wide"),
  ("GROUP BY ALL", "SELECT country, count(*) AS n, sum(population) AS p FROM staging.cities GROUP BY ALL"),
  ("filter + expression", "SELECT upper(name) AS u, population / 10 AS d FROM staging.cities WHERE year >= 2010 AND country = 'US'"),
  ("table macro", "SELECT * FROM big_cities(1000)"),
  ("join", "SELECT c.name, w.id FROM staging.cities c JOIN staging.wide w ON c.year = w.id"),
  ("window + QUALIFY", "SELECT name, year FROM staging.cities QUALIFY row_number() OVER (PARTITION BY country ORDER BY year DESC) = 1"),
]
for title, sql in CASES:
    print(f"=== {title}\n  original : {sql}")
    try:
        p = plan(sql)
        gen, names = node(p)
        a = duck(f"SELECT * FROM ({sql}) ORDER BY ALL;"); b = duck(f"SELECT * FROM ({gen}) ORDER BY ALL;")
        same = (a == b)
        print(f"  generated: {gen[:400]}")
        print(f"  result equal to the original: {same}")
        if not same: print("   orig:", a[:150].replace('\n', ' | '), "\n   gen :", b[:150].replace('\n', ' | '))
    except Unsupported as u:
        print(f"  not supported by the prototype: {u} (operator types seen: {p['type']} -> {[c['type'] for c in p.get('children', [])]})")
    except Exception as ex:
        print("  error:", type(ex).__name__, str(ex)[:150])
