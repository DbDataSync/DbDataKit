-- One row per price period of a product: it starts on its first selling day, changes every year, and the last period has no end.
SELECT p.product_id,
       p.sell_start_date + to_days(365 * k) AS start_date,
       CASE WHEN k < price_steps(p.product_id) THEN p.sell_start_date + to_days(365 * (k + 1) - 1) END AS end_date,
       CAST(round(p.standard_cost / pow(1.06, price_steps(p.product_id) - k), 2) AS DECIMAL(19, 4)) AS standard_cost
FROM production.product p
CROSS JOIN generate_series(0, 2) AS g(k)
WHERE k <= price_steps(p.product_id)
