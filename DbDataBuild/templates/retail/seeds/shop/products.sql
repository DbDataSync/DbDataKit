-- Sixty products across the six categories, with a price, a cost of 40 to 70 percent of it, and a weight.
WITH p AS (SELECT id, ['TENT', 'STOV', 'BAGS', 'BOOT', 'JACK', 'ACCS'][1 + (id % 6)] AS category_code, 10 + pick(id, 11, 390) AS price FROM range(1, 61) AS t(id))
SELECT id AS product_id,
       'NS-' || category_code || '-' || lpad(CAST(id AS VARCHAR), 3, '0') AS sku,
       'Northstar ' || category_code || ' ' || id AS product_name,
       category_code,
       CAST(price AS DECIMAL(10, 2)) AS unit_price,
       CAST(round(price * (0.4 + rnd(id, 12) * 0.3), 2) AS DECIMAL(10, 2)) AS unit_cost,
       200 + pick(id, 13, 4800) AS weight_grams,
       rnd(id, 14) < 0.95 AS active
FROM p
