-- A monthly budget for every region and category in 2024: a base that depends on the region's size, varied by up to 20 percent.
WITH m AS (SELECT 202400 + month AS budget_month FROM range(1, 13) AS t(month)),
     r AS (SELECT * FROM (VALUES ('North America', 9000), ('Europe', 6500), ('Asia Pacific', 4000), ('Latin America', 1500)) AS t(region, base)),
     c AS (SELECT category_code FROM shop.products GROUP BY category_code)
SELECT m.budget_month, r.region, c.category_code,
       CAST(round(r.base * (0.8 + rnd(m.budget_month * 10 + length(r.region), hash(c.category_code)) * 0.4), 0) AS DECIMAL(12, 2)) AS budget_amount
FROM m CROSS JOIN r CROSS JOIN c
