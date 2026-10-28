-- The sales: a day within the calendar's three years (2024-01-01 to 2026-12-25) for each.
SELECT i AS sale_id,
       CAST(DATE '2024-01-01' + pick(i, 1, 1090) AS DATE) AS sold_on,
       1 + pick(i, 2, 5) AS units,
       CAST(round(4 + rnd(i, 3) * 96, 2) AS DECIMAL(12, 2)) AS amount
FROM range(1, 1 + getvariable('scale')) AS t(i)
