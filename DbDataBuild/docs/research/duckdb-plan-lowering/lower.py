"""
Plan lowering prototype, round 2: turn DuckDB's *bound, unoptimized* logical plan into one readable SELECT in DuckDB dialect (core SQL: no macros, no PIVOT,
no star, explicit types), ready to transpile to a target. Read-only research code: it exercises how far "generate target SQL from the plan" can go.

Design: every plan operator becomes a `Rel` (a SELECT block under construction). Operators merge into the block of their child when SQL allows it (a projection over a
filter over a join is one SELECT ... FROM ... JOIN ... WHERE ...), otherwise the child is wrapped as a subquery. Column references carry markers so a block with one
source prints bare names and a block with several prints `source.column`.
"""
import json, re, subprocess, sys

SEED = open('../../../spike/seed.duckdb.sql').read()

def duck(sql, setup=SEED):
    r = subprocess.run(['duckdb', '-noheader', '-list', '-c', setup + "\n" + sql], capture_output=True, text=True)
    return (r.stdout + r.stderr).strip()

def plan(sql, setup=SEED):
    out = duck(f"SELECT json_serialize_plan($q${sql}$q$, optimize := false, skip_null := true, skip_empty := true);", setup)
    last = out.splitlines()[-1] if out else ''
    try:
        return json.loads(last)['plans'][0]
    except Exception:
        raise Unsupported('the plan could not be produced: ' + out[:120].replace('\n', ' '))

class Unsupported(Exception): pass

RESERVED = {'order', 'group', 'select', 'from', 'where', 'table', 'user', 'key', 'index', 'primary', 'end', 'by', 'case', 'when', 'then', 'else', 'all', 'as', 'to',
            'limit', 'offset', 'having', 'union', 'join', 'on', 'and', 'or', 'not', 'null', 'true', 'false', 'in', 'is', 'like', 'between', 'exists', 'distinct', 'values', 'year', 'date', 'time'}

def ident(n):
    return n if re.fullmatch(r'[a-z_][a-z0-9_]*', n) and n not in RESERVED else '"' + n.replace('"', '""') + '"'

def typ(t):
    i = t['id']; ti = t.get('type_info') or {}
    if i == 'DECIMAL': return f"DECIMAL({ti.get('width', 18)}, {ti.get('scale', 3)})"
    if i == 'VARCHAR': return 'VARCHAR'
    return i

INFIX = {'+', '-', '*', '/', '%', '||', '//', '**'}
FUNC_MAP = {'~~': 'LIKE', '~~*': 'ILIKE', '!~~': 'NOT LIKE', '!~~*': 'NOT ILIKE'}
AGG_MAP = {'count_star': 'count', 'sum_no_overflow': 'sum'}
CMP = {'COMPARE_EQUAL': '=', 'COMPARE_NOTEQUAL': '<>', 'COMPARE_LESSTHAN': '<', 'COMPARE_GREATERTHAN': '>', 'COMPARE_LESSTHANOREQUALTO': '<=',
       'COMPARE_GREATERTHANOREQUALTO': '>=', 'COMPARE_NOT_DISTINCT_FROM': 'IS NOT DISTINCT FROM', 'COMPARE_DISTINCT_FROM': 'IS DISTINCT FROM'}

def literal(v):
    if v.get('is_null'): return 'NULL'
    ty = v['type']['id']; x = v['value']
    if ty == 'DECIMAL':
        sc = (v['type'].get('type_info') or {}).get('scale', 0)
        s = str(abs(x)).rjust(sc + 1, '0')
        return ('-' if x < 0 else '') + (s[:-sc] + '.' + s[-sc:] if sc else s)
    if ty == 'VARCHAR': return "'" + x.replace("'", "''") + "'"
    if ty == 'BOOLEAN': return 'TRUE' if x in (True, 't', 'true', 1) else 'FALSE'
    if ty == 'DATE': return f"DATE '{x}'" if isinstance(x, str) else f"CAST({x} AS DATE)"
    if ty in ('TINYINT', 'SMALLINT', 'INTEGER', 'BIGINT', 'HUGEINT', 'UTINYINT', 'USMALLINT', 'UINTEGER', 'UBIGINT'): return str(x)
    if ty in ('DOUBLE', 'FLOAT'): return repr(float(x))
    raise Unsupported(f"literal of type {ty}")

class Col:
    """a column reference: rendered `alias.name`, or bare when the block has one source."""
    __slots__ = ('alias', 'name')
    def __init__(self, alias, name): self.alias, self.name = alias, name
    def token(self): return f"\x00{self.alias}\x01{self.name}\x00"

def expr(e, outs):
    """outs: the SQL (token) text of each output column of the child relation."""
    t = e['type']
    if t == 'BOUND_REF': return outs[e['index']]
    if t == 'VALUE_CONSTANT': return literal(e['value'])
    if t == 'OPERATOR_CAST':
        c = e['child']; to = e['return_type']['id']
        # constants the binder widened to the context type read better as plain literals; the engine types them by context
        if c['type'] == 'VALUE_CONSTANT' and not c['value'].get('is_null'):
            lit = c['value']['type']['id']
            if lit == to and to in ('VARCHAR', 'INTEGER', 'BIGINT', 'SMALLINT', 'BOOLEAN', 'DOUBLE'): return literal(c['value'])
            if lit in ('INTEGER', 'BIGINT', 'SMALLINT') and to in ('INTEGER', 'BIGINT', 'SMALLINT', 'DECIMAL', 'DOUBLE', 'HUGEINT'): return literal(c['value'])
            if lit == 'VARCHAR' and to == 'VARCHAR': return literal(c['value'])
            if lit == 'VARCHAR' and to == 'BOOLEAN' and c['value']['value'] in ('t', 'f', 'true', 'false'): return 'TRUE' if c['value']['value'] in ('t', 'true') else 'FALSE'
            if lit == 'VARCHAR' and to in ('DATE', 'TIMESTAMP', 'TIME'): return f"{to} {literal(c['value'])}"
        fn = 'TRY_CAST' if e.get('try_cast') else 'CAST'
        return f"{fn}({expr(c, outs)} AS {typ(e['return_type'])})"
    if t == 'BOUND_FUNCTION':
        a = [expr(c, outs) for c in e.get('children', [])]
        n = e['name']
        if n in FUNC_MAP and len(a) == 2: return f"({a[0]} {FUNC_MAP[n]} {a[1]})"
        if n in INFIX and len(a) == 2:
            text = f"({a[0]} {n} {a[1]})"
            if e['return_type']['id'] == 'TIMESTAMP' and any(c.get('return_type', {}).get('id') == 'DATE' for c in e['children']):
                RULES_FIRED.append('date-to-timestamp')
                return f"CAST({text} AS TIMESTAMP)"
            return text
        if n == '-' and len(a) == 1: return f"(-{a[0]})"
        if n in ('to_days', 'to_hours', 'to_minutes', 'to_seconds', 'to_months', 'to_years') and len(e['children']) == 1:
            c = e['children'][0]
            # the binder turns INTERVAL 3 DAY into to_days(CAST(trunc(CAST(3 AS DOUBLE)) AS INTEGER)); fold the constant back
            while c['type'] == 'OPERATOR_CAST' or (c['type'] == 'BOUND_FUNCTION' and c['name'] == 'trunc'):
                c = c['child'] if c['type'] == 'OPERATOR_CAST' else c['children'][0]
            if c['type'] == 'VALUE_CONSTANT': return f"INTERVAL {literal(c['value'])} {n[3:-1].upper()}"
            raise Unsupported('interval from an expression')
        if n in ('list_value', 'struct_pack', 'map'): raise Unsupported(f'nested type function {n}')
        text = f"{n}({', '.join(a)})"
        if e['return_type']['id'] == 'TIMESTAMP' and any(c.get('return_type', {}).get('id') == 'DATE' for c in e.get('children', [])) and n in ('date_trunc', 'datetrunc', '+', '-'):
            RULES_FIRED.append('date-to-timestamp')
            return f"CAST({text} AS TIMESTAMP)"
        return text
    if t == 'BOUND_AGGREGATE':
        a = [expr(c, outs) for c in e.get('children', [])]
        n = AGG_MAP.get(e['name'], e['name'])
        # RULE avg-double: the result type is DOUBLE, the engines' own AVG over integers or decimals is not; say so in the query
        if e['name'] == 'avg' and e['return_type']['id'] == 'DOUBLE' and e['children'] and e['children'][0]['return_type']['id'] != 'DOUBLE':
            a = [f"CAST({a[0]} AS DOUBLE)"]
            RULES_FIRED.append('avg-double')
        body = '*' if e['name'] == 'count_star' else (('DISTINCT ' if e.get('aggregate_type') == 'DISTINCT' else '') + ', '.join(a))
        s = f"{n}({body})"
        return s + (f" FILTER (WHERE {expr(e['filter'], outs)})" if e.get('filter') else '')
    if t in CMP: return f"({expr(e['left'], outs)} {CMP[t]} {expr(e['right'], outs)})"
    if t == 'COMPARE_IN' or t == 'COMPARE_NOT_IN':
        ch = [expr(c, outs) for c in e['children']]
        return f"({ch[0]} {'NOT ' if t == 'COMPARE_NOT_IN' else ''}IN ({', '.join(ch[1:])}))"
    if t == 'OPERATOR_COALESCE': return f"coalesce({', '.join(expr(c, outs) for c in e['children'])})"
    if t == 'OPERATOR_IS_NULL': return f"({expr(e['children'][0], outs)} IS NULL)"
    if t == 'OPERATOR_IS_NOT_NULL': return f"({expr(e['children'][0], outs)} IS NOT NULL)"
    if t == 'OPERATOR_NOT': return f"(NOT {expr(e['children'][0], outs)})"
    if t == 'CONJUNCTION_AND': return '(' + ' AND '.join(expr(c, outs) for c in e['children']) + ')'
    if t == 'CONJUNCTION_OR': return '(' + ' OR '.join(expr(c, outs) for c in e['children']) + ')'
    if t == 'CASE_EXPR':
        s = 'CASE ' + ' '.join(f"WHEN {expr(c['when_expr'], outs)} THEN {expr(c['then_expr'], outs)}" for c in e['case_checks'])
        return s + (f" ELSE {expr(e['else_expr'], outs)}" if e.get('else_expr') else '') + ' END'
    if t == 'WINDOW_AGGREGATE' or t == 'WINDOW_ROW_NUMBER' or t.startswith('WINDOW_'):
        return window(e, outs)
    raise Unsupported(f"expression {t}")

FRAME = {'UNBOUNDED_PRECEDING': 'UNBOUNDED PRECEDING', 'CURRENT_ROW_ROWS': 'CURRENT ROW', 'CURRENT_ROW_RANGE': 'CURRENT ROW', 'UNBOUNDED_FOLLOWING': 'UNBOUNDED FOLLOWING'}

def window(e, outs):
    name = {'WINDOW_ROW_NUMBER': 'row_number', 'WINDOW_RANK': 'rank', 'WINDOW_RANK_DENSE': 'dense_rank', 'WINDOW_LEAD': 'lead', 'WINDOW_LAG': 'lag',
            'WINDOW_FIRST_VALUE': 'first_value', 'WINDOW_LAST_VALUE': 'last_value', 'WINDOW_NTILE': 'ntile', 'WINDOW_PERCENT_RANK': 'percent_rank', 'WINDOW_CUME_DIST': 'cume_dist'}.get(e['type'], e.get('name'))
    if name is None: raise Unsupported(f"window function {e['type']}")
    arg_list = [expr(c, outs) for c in e.get('children', [])]
    if e['type'] in ('WINDOW_LEAD', 'WINDOW_LAG'):
        if e.get('offset_expr') or e.get('default_expr'):
            arg_list.append(expr(e['offset_expr'], outs) if e.get('offset_expr') else '1')
            if e.get('default_expr'): arg_list.append(expr(e['default_expr'], outs))
    args = ', '.join(arg_list)
    parts = []
    if e.get('partitions'): parts.append('PARTITION BY ' + ', '.join(expr(p, outs) for p in e['partitions']))
    if e.get('orders'): parts.append('ORDER BY ' + ', '.join(order_item(o, outs) for o in e['orders']))
    start, end = e.get('start'), e.get('end')
    # only RANGE ... CURRENT ROW is the default frame; ROWS ... CURRENT ROW is not (they differ when rows tie), so it is printed
    default = (start in (None, 'UNBOUNDED_PRECEDING') and end in (None, 'CURRENT_ROW_RANGE')) and not e.get('start_expr') and not e.get('end_expr')
    if start and not default:
        def bound(kind, ex):
            if kind in FRAME: return FRAME[kind]
            if kind.startswith('EXPR_PRECEDING'): return f"{expr(ex, outs)} PRECEDING"
            if kind.startswith('EXPR_FOLLOWING'): return f"{expr(ex, outs)} FOLLOWING"
            raise Unsupported(f'frame bound {kind}')
        unit = 'ROWS' if (start.endswith('ROWS') or end.endswith('ROWS')) else 'RANGE'
        parts.append(f"{unit} BETWEEN {bound(start, e.get('start_expr'))} AND {bound(end, e.get('end_expr'))}")
    return f"{name}({args}) OVER ({' '.join(parts)})"

def order_item(o, outs):
    d = ' DESC' if o['type'] == 'DESCENDING' else ''
    nulls = ' NULLS FIRST' if o['null_order'] == 'NULLS_FIRST' or o['null_order'] == 'NULLS FIRST' else ''
    # DuckDB's default is NULLS LAST for ASC and NULLS FIRST for DESC in 1.x; print only what differs from the default would hide a behavior difference, so print it when not default
    default_nulls = 'NULLS LAST'
    explicit = '' if o['null_order'].replace('_', ' ') == default_nulls else f" {o['null_order'].replace('_', ' ')}"
    return f"{expr(o['expression'], outs)}{d}{explicit}"

class Rel:
    def __init__(self):
        self.sel = []        # (sql, alias or None)
        self.frm = ''
        self.sources = []    # aliases visible in this block
        self.where, self.group, self.having, self.order = [], [], [], []
        self.limit = self.offset = None
        self.distinct = False
        self.has_window = False
        self.has_agg = False
        self.plain = False   # a bare table scan (nothing but the FROM)
        self.setop = None

    def outs(self): return [s for s, _ in self.sel]

    def mergeable(self):
        return not (self.limit is not None or self.offset is not None or self.distinct or self.setop)

    def _render_tokens(self, text):
        multi = len(self.sources) > 1
        def sub(m): return (f"{ident(m.group(1))}." if multi else '') + ident(m.group(2))
        return re.sub(r'\x00([^\x01\x00]*)\x01([^\x00]*)\x00', sub, text)

    def aliases(self):
        used, out = set(), []
        for i, (s, a) in enumerate(self.sel):
            m = re.fullmatch(r'\x00[^\x01\x00]*\x01([^\x00]*)\x00', s)
            n = a or (m.group(1) if m else f"col{i + 1}")
            base, k = n, 1
            while n in used: k += 1; n = f"{base}_{k}"
            used.add(n); out.append(n)
        return out

    def sql(self):
        if self.setop: return self.setop
        names = self.aliases()
        items = []
        for (s, a), n in zip(self.sel, names):
            m = re.fullmatch(r'\x00[^\x01\x00]*\x01([^\x00]*)\x00', s)
            keep = (a is not None and a != (m.group(1) if m else None)) or (m is None)
            items.append(self._render_tokens(s) + (f" AS {ident(n)}" if keep else (f" AS {ident(n)}" if m and m.group(1) != n else '')))
        q = f"SELECT {'DISTINCT ' if self.distinct else ''}{', '.join(items)}" + (f"\nFROM {self._render_tokens(self.frm)}" if self.frm else '')
        if self.where: q += "\nWHERE " + self._render_tokens(' AND '.join(self.where))
        if self.group: q += "\nGROUP BY " + self._render_tokens(', '.join(self.group))
        if self.having: q += "\nHAVING " + self._render_tokens(' AND '.join(self.having))
        if self.order: q += "\nORDER BY " + self._render_tokens(', '.join(self.order))
        if self.limit is not None: q += f"\nLIMIT {self.limit}"
        if self.offset: q += f" OFFSET {self.offset}"
        return q

_alias_n = [0]
def fresh(prefix='s'):
    _alias_n[0] += 1
    return f"{prefix}{_alias_n[0]}"

def wrap(rel):
    """the relation as a subquery: a new single-source block selecting its columns by name"""
    a = fresh()
    r = Rel()
    names = rel.aliases() if not rel.setop else rel.setop_names
    r.frm = f"(\n{indent(rel.sql())}\n) AS {a}"
    r.sources = [a]
    r.sel = [(Col(a, n).token(), n) for n in names]
    r.plain = False
    return r

def indent(s): return '\n'.join('  ' + l for l in s.splitlines())

def table_name(p):
    fd = p['function_data']
    return fd['schema'] + '.' + fd['table'] if fd.get('schema') not in (None, 'main') else fd['table']

TABLE_USES = {}
CTES = {}
RULES_FIRED = []

def node(p):
    t = p['type']
    if t == 'LOGICAL_GET':
        if 'table' not in p.get('function_data', {}): raise Unsupported('table function')
        fd = p['function_data']; names = p['names']
        idx = [c['index'] for c in p.get('column_indexes', [])] or list(range(len(names)))
        base = fd['table']
        TABLE_USES[base] = TABLE_USES.get(base, 0) + 1
        alias = base if TABLE_USES[base] == 1 else f"{base}_{TABLE_USES[base]}"
        r = Rel()
        r.frm = table_name(p) + ('' if alias == base else f" AS {alias}")
        r.sources = [alias]
        r.sel = [(Col(alias, names[i]).token(), names[i]) for i in idx]
        r.plain = True
        return r
    if t == 'LOGICAL_DUMMY_SCAN':
        return Rel()
    if t == 'LOGICAL_PROJECTION':
        c = node(p['children'][0])
        if not c.mergeable() or c.has_window and False: c = wrap(c)
        outs = c.outs()
        new = [(expr(e, outs), e.get('alias')) for e in p['expressions']]
        c.sel = new
        c.plain = False
        if any('OVER (' in s for s, _ in new): c.has_window = True
        return c
    if t == 'LOGICAL_FILTER':
        c = node(p['children'][0])
        if c.has_window or not c.mergeable(): c = wrap(c)
        outs = c.outs()
        preds = [expr(e, outs) for e in p['expressions']]
        if c.has_agg and not c.having and c.group is not None and not c.plain and c.sel and c.has_agg: c.having += preds
        else: c.where += preds
        return c
    if t == 'LOGICAL_AGGREGATE_AND_GROUP_BY':
        c = node(p['children'][0])
        if not c.mergeable() or c.has_window or c.group or c.having: c = wrap(c)
        outs = c.outs()
        gs = [expr(g, outs) for g in p.get('groups', [])]
        ags = [expr(a, outs) for a in p.get('expressions', [])]
        c.group = gs
        c.sel = [(g, None) for g in gs] + [(a, None) for a in ags]
        c.has_agg = True
        c.plain = False
        if not gs and not ags: raise Unsupported('empty aggregate')
        return c
    if t == 'LOGICAL_ORDER_BY':
        c = node(p['children'][0])
        if not c.mergeable() and (c.limit is not None): c = wrap(c)
        c.order = [order_item(o, c.outs()) for o in p['orders']]
        return c
    if t == 'LOGICAL_LIMIT':
        c = node(p['children'][0])
        if c.limit is not None or c.offset is not None: c = wrap(c)
        def val(k):
            v = p.get(k)
            if v is None or v.get('type') == 'UNSET': return None
            if v.get('type') == 'CONSTANT_VALUE' and v.get('constant_percentage', -1) == -1: return v['constant_integer']
            raise Unsupported('limit that is not a constant')
        c.limit = val('limit_val')
        c.offset = val('offset_val')
        return c
    if t == 'LOGICAL_DISTINCT':
        c = node(p['children'][0])
        if p.get('distinct_type') != 'DISTINCT': raise Unsupported('DISTINCT ON')
        if c.setop and getattr(c, 'setop_distinct', False): return c       # a set operation without ALL is already distinct
        if not c.mergeable() or c.has_window: c = wrap(c)
        c.distinct = True
        return c
    if t in ('LOGICAL_COMPARISON_JOIN', 'LOGICAL_CROSS_PRODUCT'):
        l, r = node(p['children'][0]), node(p['children'][1])
        for x in (l, r):
            pass
        if not (l.plain and r.plain) :
            if not l.plain: l = wrap(l)
            if not r.plain: r = wrap(r)
        out = Rel()
        out.sources = l.sources + r.sources
        if t == 'LOGICAL_CROSS_PRODUCT':
            out.frm = f"{l.frm}\nCROSS JOIN {r.frm}"
            out.sel = l.sel + r.sel
            return out
        jt = {'INNER': 'JOIN', 'LEFT': 'LEFT JOIN', 'RIGHT': 'RIGHT JOIN', 'OUTER': 'FULL JOIN', 'FULL': 'FULL JOIN'}.get(p['join_type'])
        if jt is None: raise Unsupported(f"join type {p['join_type']}")
        lo, ro = l.outs(), r.outs()
        conds = [f"({expr(c['left'], lo)} {CMP[c['comparison']]} {expr(c['right'], ro)})" for c in p['conditions']]
        if p.get('expression'): raise Unsupported('join with extra expression')
        out.frm = f"{l.frm}\n{jt} {r.frm}\n  ON {' AND '.join(conds)}"
        out.sel = l.sel + r.sel
        return out
    if t == 'LOGICAL_WINDOW':
        c = node(p['children'][0])
        if not c.mergeable() or c.has_agg and False: c = wrap(c)
        outs = c.outs()
        c.sel = c.sel + [(window(e, outs), None) for e in p['expressions']]
        c.has_window = True
        return c
    if t == 'LOGICAL_MATERIALIZED_CTE':
        definition = node(p['children'][0])
        CTES[p['table_index']] = (p['ctename'], definition)
        main = node(p['children'][1])
        main.ctes = [(p['ctename'], definition.sql())] + getattr(main, 'ctes', [])
        return main
    if t == 'LOGICAL_CTE_REF':
        name, definition = CTES[p['cte_index']]
        a = TABLE_USES.get('cte:' + name, 0) + 1
        TABLE_USES['cte:' + name] = a
        alias = name if a == 1 else f"{name}_{a}"
        r = Rel()
        r.frm = name + ('' if alias == name else f" AS {alias}")
        r.sources = [alias]
        names = definition.aliases() if not definition.setop else definition.setop_names
        r.sel = [(Col(alias, n).token(), n) for n in names]
        r.plain = True
        return r
    if t in ('LOGICAL_UNION', 'LOGICAL_INTERSECT', 'LOGICAL_EXCEPT'):
        a, b = node(p['children'][0]), node(p['children'][1])
        r = Rel()
        op = {'LOGICAL_UNION': 'UNION', 'LOGICAL_INTERSECT': 'INTERSECT', 'LOGICAL_EXCEPT': 'EXCEPT'}[t]
        r.setop = f"{a.sql()}\n{op}{' ALL' if p.get('setop_all') else ''}\n{b.sql()}"
        r.setop_distinct = not p.get('setop_all')
        r.setop_names = a.aliases()
        r.sel = [(Col('u', n).token(), n) for n in r.setop_names]
        return r
    raise Unsupported(f"operator {t}")

def lower(sql, setup=SEED):
    _alias_n[0] = 0; TABLE_USES.clear(); CTES.clear()
    rel = node(plan(sql, setup))
    body = rel.sql()
    ctes = getattr(rel, 'ctes', [])
    return ('WITH ' + ',\n'.join(f"{n} AS (\n{indent(q)}\n)" for n, q in ctes) + '\n' if ctes else '') + body
