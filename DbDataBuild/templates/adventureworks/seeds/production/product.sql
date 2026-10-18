-- 150 products: 1 to 30 are the bikes (what is built), 31 to 90 components, 91 to 150 clothing and accessories. The cost and the list price are the current ones: each was raised 0 to 2 times
-- over the years, and the history tables walk back from here.
WITH p AS (
  SELECT i AS product_id,
         CASE WHEN i <= 30 THEN 1 + pick(i, 61, 3) WHEN i <= 90 THEN 4 + pick(i, 61, 3) ELSE 7 + pick(i, 61, 6) END AS product_subcategory_id,
         CASE WHEN i <= 30 THEN 400 + rnd(i, 62) * 1200 WHEN i <= 90 THEN 5 + rnd(i, 62) * 245 ELSE 8 + rnd(i, 62) * 52 END AS base_cost
  FROM generate_series(1, 150) AS g(i)
)
SELECT p.product_id,
       s.name || ' ' || (100 + p.product_id) AS name,
       ['BK', 'CP', 'CL', 'AC'][s.product_category_id] || '-' || lpad(CAST(p.product_id AS VARCHAR), 4, '0') AS product_number,
       p.product_id <= 90 AS make_flag,
       CASE WHEN rnd(p.product_id, 63) < 0.2 THEN NULL ELSE ['Black', 'Red', 'Silver', 'Blue', 'Yellow'][1 + pick(p.product_id, 64, 5)] END AS color,
       CASE WHEN s.product_category_id = 3 THEN ['S', 'M', 'L', 'XL'][1 + pick(p.product_id, 65, 4)] END AS size,
       CASE WHEN s.product_category_id = 3 THEN NULL ELSE CAST(round(0.2 + rnd(p.product_id, 66) * 14, 2) AS DECIMAL(8, 2)) END AS weight,
       CAST(round(p.base_cost * pow(1.06, price_steps(p.product_id)), 2) AS DECIMAL(19, 4)) AS standard_cost,
       CAST(round(p.base_cost * pow(1.06, price_steps(p.product_id)) * (1.35 + 0.5 * rnd(p.product_id, 67)), 2) AS DECIMAL(19, 4)) AS list_price,
       p.product_subcategory_id,
       DATE '2019-01-01' + to_days(pick(p.product_id, 68, 300)) AS sell_start_date,
       CASE WHEN rnd(p.product_id, 69) < 0.08 THEN DATE '2024-06-30' END AS sell_end_date,
       -- a JSON document of attributes: a frame for the bikes, tags for everything, and none for one product in ten
       CASE WHEN rnd(p.product_id, 56) < 0.10 THEN NULL
            WHEN p.product_id <= 30 THEN '{"frame": {"material": "' || ['alloy', 'carbon', 'steel', 'titanium'][1 + pick(p.product_id, 57, 4)] || '", "gears": ' || (1 + pick(p.product_id, 58, 24)) || '}, "tags": ["' ||
                                         ['road', 'trail', 'city', 'race'][1 + pick(p.product_id, 59, 4)] || '", "' || ['new', 'sale', 'popular'][1 + pick(p.product_id, 60, 3)] || '"]}'
            ELSE '{"tags": ["' || ['basic', 'premium', 'seasonal'][1 + pick(p.product_id, 59, 3)] || '"]}' END AS attributes
FROM p
JOIN production.product_subcategory s ON s.product_subcategory_id = p.product_subcategory_id
