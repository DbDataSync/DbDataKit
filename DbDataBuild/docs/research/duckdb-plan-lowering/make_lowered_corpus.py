"""Writes a spike root (<out>/spike/constructs.yml + seed) whose cases are the *lowered* SQL of every case that lowered result-equal, so the spike runner can execute them on real engines."""
import json, os, shutil, sys
out = sys.argv[1]
rows = json.load(open('corpus-results.json'))
os.makedirs(out + '/spike', exist_ok=True)
shutil.copy('../../../spike/seed.duckdb.sql', out + '/spike/seed.duckdb.sql')
with open(out + '/spike/constructs.yml', 'w') as f:
    for r in rows:
        if r['status'] == 'equal' and r['id'] != 'current_ts':
            sql = ' '.join(r['lowered'].split())
            f.write('- { id: %s, sql: %s, expect: translate }\n' % (r['id'], json.dumps(sql)))
print(sum(1 for r in rows if r['status'] == 'equal' and r['id'] != 'current_ts'), 'cases written to', out)
