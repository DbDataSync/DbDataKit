-- 1,500 work orders for the bikes over three years; some are in progress (no end date), and a few percent of what is built is scrapped.
SELECT i AS work_order_id,
       1 + pick(i, 81, 30) AS product_id,
       5 + pick(i, 82, 95) AS order_qty,
       CAST(floor((5 + pick(i, 82, 95)) * pow(rnd(i, 84), 3) * 0.15) AS INTEGER) AS scrapped_qty,
       DATE '2022-01-03' + to_days(pick(i, 83, 1090)) AS start_date,
       CASE WHEN rnd(i, 85) < 0.04 THEN NULL ELSE DATE '2022-01-03' + to_days(pick(i, 83, 1090) + 4 + pick(i, 86, 28)) END AS end_date,
       DATE '2022-01-03' + to_days(pick(i, 83, 1090) + 14 + pick(i, 87, 10)) AS due_date
FROM generate_series(1, 1500) AS g(i)
