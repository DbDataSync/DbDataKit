"""Compare the spike results of the original cases with those of their lowered form: per engine, how many match, and which cells changed."""
import sys
def table(path):
    rows = {}
    for l in open(path):
        if l.startswith('| ') and not l.startswith('| id') and not l.startswith('|---'):
            c = [x.strip() for x in l.strip().strip('|').split('|')]
            rows[c[0]] = dict(zip(['sqlserver', 'fabric', 'postgres'], c[2:5]))
    return rows
orig, low = table(sys.argv[1]), table(sys.argv[2])
ids = [i for i in low if i in orig]
print(f"{len(ids)} cases compared")
for eng in ('sqlserver', 'postgres'):
    o = sum(1 for i in ids if orig[i][eng] == 'MATCH'); n = sum(1 for i in ids if low[i][eng] == 'MATCH')
    print(f"  {eng:10} original MATCH {o:3}   lowered MATCH {n:3}")
    for i in ids:
        if orig[i][eng] != low[i][eng]:
            arrow = 'IMPROVED' if low[i][eng] == 'MATCH' else ('REGRESSED' if orig[i][eng] == 'MATCH' else 'changed')
            print(f"      {arrow:9} {i}: {orig[i][eng]} -> {low[i][eng]}")
