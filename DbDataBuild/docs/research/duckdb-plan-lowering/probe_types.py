"""What does the bound plan say about types? Prints, for each probe, the output type of every projected expression and the function bindings used."""
import json, subprocess, sys
SETUP = open('setup.sql').read() + """
CREATE TABLE staging.t (a INTEGER, b INTEGER, s VARCHAR, d DATE, ts TIMESTAMP, n DECIMAL(10,2), f DOUBLE);
"""
def duck(sql):
    r = subprocess.run(['duckdb', '-noheader', '-list', '-c', SETUP + "\n" + sql], capture_output=True, text=True)
    return (r.stdout + r.stderr).strip()
def plan(sql):
    out = duck(f"SELECT json_serialize_plan($q${sql}$q$, optimize := false, skip_null := true, skip_empty := true);")
    return json.loads(out.splitlines()[-1])['plans'][0]
def tname(t):
    ti = t.get('type_info') or {}
    if t['id'] == 'DECIMAL': return f"DECIMAL({ti.get('width')},{ti.get('scale')})"
    if t['id'] == 'VARCHAR': return 'VARCHAR'
    return t['id']
def walk(e, depth=0, out=None):
    out = out if out is not None else []
    out.append(('  ' * depth) + f"{e['type']} {e.get('name', '')} -> {tname(e['return_type'])}" if 'return_type' in e else ('  ' * depth) + e['type'])
    for k in ('children', ):
        for c in e.get(k, []): walk(c, depth + 1, out)
    for k in ('child', 'left', 'right'):
        if k in e: walk(e[k], depth + 1, out)
    return out
def find_proj(p):
    if p['type'] == 'LOGICAL_PROJECTION': return p
    for c in p.get('children', []):
        r = find_proj(c)
        if r: return r
PROBES = [
 "SELECT a / b AS q FROM staging.t",
 "SELECT a // b AS q FROM staging.t",
 "SELECT avg(a) AS m FROM staging.t",
 "SELECT length(s) AS l FROM staging.t",
 "SELECT s || s AS c FROM staging.t",
 "SELECT try_cast(s AS INTEGER) AS x FROM staging.t",
 "SELECT date_trunc('month', d) AS m FROM staging.t",
 "SELECT d + INTERVAL 3 DAY AS x FROM staging.t",
 "SELECT n * n AS sq, n + 1 AS inc, n / 3 AS dv FROM staging.t",
 "SELECT a + b AS s, a::BIGINT * b AS m FROM staging.t",
 "SELECT s LIKE 'a%' AS l, s ILIKE 'a%' AS il, s = 'x' AS e FROM staging.t",
 "SELECT CAST(a AS VARCHAR(20)) AS v FROM staging.t",
 "SELECT coalesce(a, 0) AS c, ifnull(f, 0) AS i FROM staging.t",
]
for sql in PROBES:
    print("===", sql)
    try:
        for e in find_proj(plan(sql))['expressions']:
            print('\n'.join(walk(e)))
    except Exception as ex:
        print("  error:", type(ex).__name__, str(ex)[:200])
