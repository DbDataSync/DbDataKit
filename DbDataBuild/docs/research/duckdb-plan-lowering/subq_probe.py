"""Plan shapes of subqueries (bound, unoptimized): which operators and join types DuckDB produces."""
import json, sys
sys.path.insert(0, '.')
import lower
def show(p, d=0):
    extra = {k: v for k, v in p.items() if k in ('join_type', 'delim_flipped', 'duplicate_eliminated_columns', 'mark_index', 'column_idx', 'chunk_types', 'setop_all', 'table_index', 'delim_index')}
    keys = [k for k in p if k not in ('type', 'children', 'names', 'returned_types', 'function_data', 'column_indexes', 'extra_info', 'ordinality_idx', 'has_serialize', 'name')]
    print('  ' * d + p['type'], {k: (v if not isinstance(v, (list, dict)) else '..') for k, v in p.items() if k in ('join_type', 'mark_index', 'delim_flipped', 'table_index', 'setop_all')}, 'keys:', [k for k in keys if k not in ('join_type', 'mark_index', 'delim_flipped', 'table_index', 'setop_all')][:8])
    for c in p.get('children', []): show(c, d + 1)
QS = [
 "SELECT a FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a)",
 "SELECT a FROM t WHERE NOT EXISTS (SELECT 1 FROM u WHERE u.a = t.a)",
 "SELECT a FROM t WHERE a IN (SELECT a FROM u)",
 "SELECT a FROM t WHERE a NOT IN (SELECT a FROM u)",
 "SELECT a FROM t WHERE a IN (SELECT u.a FROM u WHERE u.b > t.b)",
 "SELECT a, (SELECT max(u.b) FROM u WHERE u.a = t.a) AS m FROM t",
 "SELECT a, (SELECT max(b) FROM u) AS m FROM t",
 "SELECT a, EXISTS (SELECT 1 FROM u WHERE u.a = t.a) AS e FROM t",
 "SELECT t.a, x.b FROM t, LATERAL (SELECT u.b FROM u WHERE u.a = t.a) x",
 "SELECT a FROM t WHERE b > (SELECT avg(b) FROM u WHERE u.a = t.a)",
]
for q in QS:
    print("===", q)
    try: show(lower.plan(q))
    except Exception as e: print('  ERR', e)
