"""Tally operators, expression kinds and function names of the bound (unoptimized) plan
for every case in spike/constructs.yml. Run from this directory: python3 surface.py"""
import collections, json, re, subprocess

ROOT = '../../../spike/'
SETUP = open(ROOT + 'seed.duckdb.sql').read()

def serialize(sql):
    q = f"SELECT json_serialize_plan($q${sql}$q$, optimize := false, skip_null := true, skip_empty := true);"
    r = subprocess.run(['duckdb', '-noheader', '-list', '-c', SETUP + "\n" + q], capture_output=True, text=True)
    return json.loads(r.stdout.strip().splitlines()[-1])

def walk(o, ops, kinds, funcs):
    if isinstance(o, dict):
        if 'type' in o and isinstance(o['type'], str):
            if o['type'].startswith('LOGICAL_'): ops[o['type'][8:]] += 1
            elif o['type'].startswith(('BOUND_', 'VALUE_', 'OPERATOR_', 'COMPARE_')): kinds[o['type']] += 1
        if o.get('class') == 'BOUND_FUNCTION' or 'function_name' in o:
            funcs[o.get('name', o.get('function_name'))] += 1
        for v in o.values(): walk(v, ops, kinds, funcs)
    elif isinstance(o, list):
        for v in o: walk(v, ops, kinds, funcs)

# constructs.yml: one flow mapping per line, `sql: "..."` with no embedded double quotes.
text = open(ROOT + 'constructs.yml').read()
cases = re.findall(r'sql:\s*"([^"]*)"', text)
ops, kinds, funcs, failed = collections.Counter(), collections.Counter(), collections.Counter(), 0
for sql in cases:
    try: walk(serialize(sql), ops, kinds, funcs)
    except Exception: failed += 1
print(len(cases), 'cases;', failed, 'failed to bind')
for title, c in (('operators', ops), ('expression kinds', kinds), ('functions', funcs)):
    print('\n' + title)
    for k, n in c.most_common(): print(f'  {n:5} {k}')
