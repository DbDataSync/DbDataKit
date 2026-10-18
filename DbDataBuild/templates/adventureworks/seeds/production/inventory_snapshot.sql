-- Stock on the last day of each month of 2023 and 2024, for every product.
SELECT CAST(date_trunc('month', DATE '2023-01-01' + to_months(m)) + INTERVAL 1 MONTH - INTERVAL 1 DAY AS DATE) AS snapshot_date,
       p AS product_id,
       pick(p * 100 + m, 88, 500) AS quantity
FROM generate_series(0, 23) AS g(m)
CROSS JOIN generate_series(1, 150) AS h(p)
