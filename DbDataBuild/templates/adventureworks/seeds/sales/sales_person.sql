-- Twelve sales people (employees 1 to 12): ten work a territory, two the whole world, and two have no quota.
SELECT i AS sales_person_id,
       CASE WHEN i <= 10 THEN i END AS territory_id,
       CASE WHEN i IN (5, 11) THEN NULL ELSE CAST(250000 + 50000 * (i % 4) AS DECIMAL(19, 4)) END AS sales_quota,
       CAST(1000 + 500 * pick(i, 101, 8) AS DECIMAL(19, 4)) AS bonus,
       CAST(round(0.005 + 0.01 * rnd(i, 102), 4) AS DECIMAL(10, 4)) AS commission_pct
FROM generate_series(1, 12) AS g(i)
