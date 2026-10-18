import subprocess, re, json, collections, glob
seed=open('spike/seed.duckdb.sql').read()
cases=[]
for line in open('spike/constructs.yml').read().splitlines():
    m=re.match(r'^- \{ id: (\w+),\s+sql: "(.*)",\s+expect: \w+ \}\s*$',line)
    if m: cases.append(m.group(2).replace('\\"','"'))
# subquery/series/window/distinct-on forms from the unit tests
src=open('tests/DbDataBuild.Tests.Unit/PlanLowererTests.cs').read()
for m in re.finditer(r'(?:InlineData|\[InlineData)\("((?:SELECT|WITH)[^"]*(?:\\"[^"]*)*)"', src):
    cases.append(m.group(1).replace('\\"','"'))
cases=list(dict.fromkeys(cases))
print(len(cases),'queries')
def plan(cli,sql):
    q="SELECT json_serialize_plan('"+sql.rstrip(';').replace("'","''")+"', optimize := false, skip_null := true, skip_empty := true);"
    p=subprocess.run([cli,'-batch','-noheader','-list']+(['-no-agent'] if cli==os.environ.get('DUCKDB_NEW','.build/duckdb-preview/duckdb') else []),input=seed+"\n"+q,capture_output=True,text=True,timeout=60)
    out=p.stdout.strip().splitlines()
    try: return json.loads(out[-1])
    except Exception: return None
def kinds(n,acc,path=''):
    if isinstance(n,dict):
        k=n.get('type') if isinstance(n.get('type'),str) else None
        ec=n.get('expression_class')
        if k or ec:
            key=(ec or '', k or '')
            acc[key].update(n.keys())
            if 'qname' in n: acc[key].add('~qname')
        for v in n.values(): kinds(v,acc)
    elif isinstance(n,list):
        for v in n: kinds(v,acc)
res={}
import os
for cli,label in [(os.environ.get('DUCKDB_OLD','duckdb'),'old'),(os.environ.get('DUCKDB_NEW','.build/duckdb-preview/duckdb'),'new')]:
    acc=collections.defaultdict(set); ok=0
    for q in cases:
        p=plan(cli,q)
        if p and not p.get('error'): ok+=1; kinds(p,acc)
    res[label]=(acc,ok); print(label,'plans ok:',ok)
old,new=res['old'][0],res['new'][0]
print('\nKINDS ONLY IN 1.5:'); [print('  ',k) for k in sorted(set(old)-set(new))]
print('\nKINDS ONLY IN 2.0:'); [print('  ',k) for k in sorted(set(new)-set(old))]
print('\nKEY CHANGES ON SHARED KINDS:')
for k in sorted(set(old)&set(new)):
    a,b=old[k],new[k]
    if a-b or b-a: print('  ',k,'removed:',sorted(a-b),'added:',sorted(b-a))
