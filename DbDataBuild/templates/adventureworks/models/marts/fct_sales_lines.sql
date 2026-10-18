-- One row per order line (cancelled and rejected orders left out), in dollars: the price of an order in another currency is converted at the latest rate on or before the order date,
-- and the cost is the product's cost on that date from the cost history. Loaded incrementally by order and line.
-- The amounts are products of several decimals; a chain like that is exact in DuckDB and rounded at every step on SQL Server (see `type.decimal_product_wide` in the support matrix), so the
-- discount and the conversion are worked in DOUBLE and rounded to four places, which both engines do the same way.
WITH lines AS (
  SELECT l.sales_order_id, l.sales_order_detail_id, o.order_date, o.currency_code, o.customer_id, o.sales_person_id, o.territory_id,
         l.product_id, l.order_qty, l.unit_price, l.unit_price_discount, l.special_offer_id
  FROM staging.order_lines l
  JOIN staging.orders o ON o.sales_order_id = l.sales_order_id
  WHERE o.status_name NOT IN ('cancelled', 'rejected')
),
rated AS (
  SELECT x.*,
         coalesce((SELECT r.average_rate FROM staging.currency_rates r WHERE r.currency_code = x.currency_code AND r.rate_date <= x.order_date ORDER BY r.rate_date DESC LIMIT 1), 1) AS rate_to_usd
  FROM lines x
),
local_amounts AS (
  SELECT r.*,
         CAST(r.order_qty * r.unit_price AS DECIMAL(19, 4)) AS gross_local,
         CAST(round(CAST(r.order_qty * r.unit_price AS DOUBLE) * CAST(r.unit_price_discount AS DOUBLE), 4) AS DECIMAL(19, 4)) AS discount_local
  FROM rated r
)
SELECT a.sales_order_id,
       a.sales_order_detail_id,
       year(a.order_date) * 10000 + month(a.order_date) * 100 + day(a.order_date) AS date_key,
       a.customer_id,
       a.sales_person_id,
       a.territory_id,
       a.product_id,
       a.special_offer_id,
       a.order_qty,
       a.currency_code,
       CAST(a.rate_to_usd AS DECIMAL(10, 4)) AS rate_to_usd,
       CAST(round(CAST(a.gross_local AS DOUBLE) * CAST(a.rate_to_usd AS DOUBLE), 4) AS DECIMAL(19, 4)) AS gross_usd,
       CAST(round(CAST(a.discount_local AS DOUBLE) * CAST(a.rate_to_usd AS DOUBLE), 4) AS DECIMAL(19, 4)) AS discount_usd,
       CAST(round(CAST(a.gross_local - a.discount_local AS DOUBLE) * CAST(a.rate_to_usd AS DOUBLE), 4) AS DECIMAL(19, 4)) AS revenue_usd,
       CAST(a.order_qty * c.standard_cost AS DECIMAL(19, 4)) AS cost_usd,
       CAST(round(CAST(a.gross_local - a.discount_local AS DOUBLE) * CAST(a.rate_to_usd AS DOUBLE), 4) - a.order_qty * c.standard_cost AS DECIMAL(19, 4)) AS margin_usd
FROM local_amounts a
JOIN staging.product_cost_history c ON c.product_id = a.product_id AND a.order_date >= c.valid_from AND (c.valid_to IS NULL OR a.order_date <= c.valid_to)
