-- Four orders to a customer on average: two thirds online, the rest sold by a sales person of the customer's territory. Tax by country, freight by value, and a few orders not shipped yet.
WITH totals AS (
  SELECT sales_order_id, CAST(sum(order_qty * unit_price * (1 - unit_price_discount)) AS DECIMAL(19, 4)) AS sub_total
  FROM sales.sales_order_detail
  GROUP BY sales_order_id
),
orders AS (
  SELECT i AS sales_order_id, order_date_of(i) AS order_date, 1 + pick(i, 91, getvariable('scale')) AS customer_id, rnd(i, 104) < 0.6 AS online_order_flag
  FROM generate_series(1, getvariable('scale') * 4) AS g(i)
)
SELECT o.sales_order_id,
       o.order_date,
       o.order_date + to_days(12) AS due_date,
       CASE WHEN rnd(o.sales_order_id, 105) < 0.03 THEN NULL ELSE o.order_date + to_days(1 + pick(o.sales_order_id, 106, 10)) END AS ship_date,
       CAST(CASE WHEN rnd(o.sales_order_id, 105) < 0.01 THEN 4 WHEN rnd(o.sales_order_id, 105) < 0.02 THEN 6 WHEN rnd(o.sales_order_id, 105) < 0.03 THEN 3 ELSE 5 END AS SMALLINT) AS status,
       o.online_order_flag,
       o.customer_id,
       CASE WHEN o.online_order_flag THEN NULL ELSE coalesce(sp.sales_person_id, 11 + pick(o.sales_order_id, 107, 2)) END AS sales_person_id,
       c.territory_id,
       t.currency_code,
       totals.sub_total,
       CAST(round(totals.sub_total * CASE t.country_region_code WHEN 'US' THEN 0.0725 WHEN 'CA' THEN 0.05 WHEN 'GB' THEN 0.20 ELSE 0.19 END, 4) AS DECIMAL(19, 4)) AS tax_amt,
       CAST(round(totals.sub_total * 0.025, 4) AS DECIMAL(19, 4)) AS freight
FROM orders o
JOIN totals ON totals.sales_order_id = o.sales_order_id
JOIN sales.customer c ON c.customer_id = o.customer_id
JOIN sales.sales_territory t ON t.territory_id = c.territory_id
LEFT JOIN sales.sales_person sp ON sp.territory_id = c.territory_id
