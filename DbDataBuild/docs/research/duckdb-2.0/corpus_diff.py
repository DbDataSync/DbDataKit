import subprocess, re, sys, json
seed=open('spike/seed.duckdb.sql').read()
import os
OLD=os.environ.get('DUCKDB_OLD','duckdb'); NEW=os.environ.get('DUCKDB_NEW','.build/duckdb-preview/duckdb')   # two CLI binaries to compare
txt=open('spike/constructs.yml').read()
cases=[]
for line in txt.splitlines():
    m=re.match(r'^- \{ id: (\w+),\s+sql: "(.*)",\s+expect: \w+ \}\s*$',line)
    if m: cases.append((m.group(1), m.group(2).replace('\\"','"').replace("\\\\","\\")))
print(len(cases),'constructs')
def run(cli, sql):
    script = seed + "\n.mode csv\n.header on\n" + f"SELECT * FROM ({sql.rstrip(';')}) ORDER BY ALL;\n"
    p=subprocess.run([cli,'-batch'] + (['-no-agent'] if cli==NEW else []),input=script,capture_output=True,text=True,timeout=60)
    out=(p.stdout or '').strip()
    err=[l for l in (p.stderr or '').strip().splitlines() if not l.startswith('duckdb agent mode')]
    return out, (err[0] if err else '')
diffs=0
for id,sql in cases:
    if id=='current_ts': continue
    a=run(OLD,sql); b=run(NEW,sql)
    if a!=b:
        diffs+=1
        print('==',id,'|',sql)
        print('  1.5:',(a[0][:150].replace('\n',' / ') or '-'),'|',a[1][:120])
        print('  2.0:',(b[0][:150].replace('\n',' / ') or '-'),'|',b[1][:120])
print('different:',diffs)
