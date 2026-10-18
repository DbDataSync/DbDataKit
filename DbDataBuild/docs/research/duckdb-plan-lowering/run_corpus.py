"""Lower every case of spike/constructs.yml and report: did it lower, is the lowered query result-equal to the original in DuckDB, and how similar is it to the source."""
import difflib, re, sys, json
sys.path.insert(0, '.')
import lower

text = open('../../../spike/constructs.yml').read()
cases = re.findall(r'\{ id: (\w+),\s+sql: "([^"]*)"', text)
def tokens(s): return re.findall(r"[A-Za-z_][A-Za-z0-9_]*|\d+|'[^']*'|[^\sA-Za-z0-9_]", s)
def norm(s): return [t.lower() for t in tokens(s) if t not in ('(', ')')]
def canon(s):
    """compare queries as a person reads them: case-insensitive tokens, parentheses and AS ignored"""
    return [t for t in norm(s) if t != 'as']
def similarity(a, b): return difflib.SequenceMatcher(None, canon(a), canon(b)).ratio()

rows, unsupported = [], {}
for cid, sql in cases:
    try:
        low = lower.lower(sql)
    except lower.Unsupported as u:
        rows.append((cid, 'unsupported', str(u), sql, None, None)); unsupported[str(u)] = unsupported.get(str(u), 0) + 1; continue
    except Exception as ex:
        rows.append((cid, 'error', f"{type(ex).__name__}: {str(ex)[:100]}", sql, None, None)); continue
    a = lower.duck(f"SELECT * FROM ({sql}) ORDER BY ALL;")
    b = lower.duck(f"SELECT * FROM ({low}) ORDER BY ALL;")
    status = 'equal' if a == b else ('both_error' if a.startswith('Error') and b.startswith('Error') else 'DIFFERENT')
    rows.append((cid, status, '' if status == 'equal' else (a[:80] + ' | ' + b[:80]).replace('\n', ' '), sql, low, similarity(sql, low)))
from collections import Counter
c = Counter(r[1] for r in rows)
print(f"{len(rows)} cases:", dict(c))
sims = [r[5] for r in rows if r[5] is not None and r[1] == 'equal']
if sims:
    sims.sort()
    print(f"similarity of lowered to source (token ratio) over {len(sims)} equal cases: min {sims[0]:.2f}, median {sims[len(sims)//2]:.2f}, mean {sum(sims)/len(sims):.2f}, >=0.8: {sum(1 for s in sims if s >= .8)}")
print("unsupported reasons:", unsupported)
for r in rows:
    if r[1] in ('DIFFERENT', 'error'): print(' ', r[0], r[1], r[2], '\n     ', r[3])
json.dump([{'id': r[0], 'status': r[1], 'note': r[2], 'source': r[3], 'lowered': r[4], 'similarity': r[5]} for r in rows], open('corpus-results.json', 'w'), indent=1)
