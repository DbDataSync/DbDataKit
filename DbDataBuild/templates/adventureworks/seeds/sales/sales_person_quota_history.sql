-- A quota for every quarter of 2022 to 2024 for the sales people who have one.
SELECT sp.sales_person_id,
       DATE '2022-01-01' + INTERVAL (3 * q) MONTH AS quota_date,
       CAST(round((150000 + 200000 * rnd(sp.sales_person_id * 100 + q, 103)) / 1000) * 1000 AS DECIMAL(19, 4)) AS sales_quota
FROM sales.sales_person sp
CROSS JOIN generate_series(0, 11) AS g(q)
WHERE sp.sales_quota IS NOT NULL
