"""Frame fidelity: ROWS and RANGE frames differ when rows tie, so the lowered query must keep the frame the author wrote. Compares row by row (ordered by id)."""
import sys; sys.path.insert(0, '.')
import lower
cases = [
 "SELECT id, sum(x) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS s FROM t ORDER BY id",
 "SELECT id, sum(x) OVER (ORDER BY a RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS s FROM t ORDER BY id",
 "SELECT id, sum(x) OVER (ORDER BY a) AS s FROM t ORDER BY id",
 "SELECT id, sum(x) OVER (ORDER BY a ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING) AS s FROM t ORDER BY id",
 "SELECT id, sum(x) OVER (PARTITION BY a) AS s FROM t ORDER BY id",
]
for sql in cases:
    low = lower.lower(sql)
    a, b = lower.duck(sql), lower.duck(low)
    print('OK ' if a == b else 'DIFF', sql, '\n    ->', low.replace('\n', ' '))
