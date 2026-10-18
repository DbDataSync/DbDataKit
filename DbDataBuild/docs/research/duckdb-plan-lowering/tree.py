import json,sys
d=json.load(sys.stdin)
def expr(e):
    t=e.get('type')
    if t=='BOUND_COLUMN_REF': return f"#col{e['binding']['column_index']}"
    if t=='BOUND_CONSTANT': return repr(e['value'].get('value'))
    if t=='BOUND_FUNCTION': return e['name']+"("+", ".join(expr(c) for c in e.get('children',[]))+")"
    if t=='BOUND_CAST': return f"CAST({expr(e['child'])} AS {e['return_type']['id']})"
    if t=='BOUND_AGGREGATE': return e['name']+"("+", ".join(expr(c) for c in e.get('children',[]))+")"+(f" FILTER({expr(e['filter'])})" if e.get('filter') else "")
    if t=='BOUND_COMPARISON': return f"({expr(e['left'])} {t} {expr(e['right'])})"
    return t
def walk(p,ind=0):
    pad="  "*ind
    names=p.get('names')
    ex=[expr(x) for x in p.get('expressions',[])]
    extra=""
    if p['type']=='LOGICAL_GET': extra=f" {p.get('function_data',{}).get('table', p.get('name'))}"
    if p['type']=='LOGICAL_AGGREGATE_AND_GROUP_BY': extra=f" groups={[expr(g) for g in p.get('groups',[])]}"
    print(f"{pad}{p['type']}{extra} {ex if ex else ''}")
    for c in p.get('children',[]): walk(c,ind+1)
for p in d['plans']: walk(p)
