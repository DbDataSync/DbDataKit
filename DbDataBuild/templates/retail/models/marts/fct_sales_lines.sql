-- One row per order line, with the order's discount and shipping fee ALLOCATED to its lines in proportion to what each line cost.
-- Money is handled in whole cents so the pieces add up exactly: every line gets the rounded-down share, and the last line of the order gets
-- whatever is left, so the allocated amounts of an order always sum to the order's discount and shipping fee, to the cent.
-- (DuckDB sums whole numbers into HUGEINT, which no target has: the sums are cast back to BIGINT.)
WITH lines AS (
  SELECT l.order_id,
         l.line_no,
         l.product_id,
         l.quantity,
         CAST(round(l.line_amount * 100) AS BIGINT) AS gross_cents,
         CAST(round(l.quantity * p.unit_cost * 100) AS BIGINT) AS cost_cents,
         CAST(round(o.discount_amount * 100) AS BIGINT) AS order_discount_cents,
         CAST(round(o.shipping_fee * 100) AS BIGINT) AS order_shipping_cents,
         o.customer_id,
         o.store_id,
         o.order_date
  FROM staging.order_lines l
  JOIN staging.orders o ON o.order_id = l.order_id
  JOIN staging.products p ON p.product_id = l.product_id
  WHERE o.status <> 'cancelled'
),
shares AS (
  SELECT *,
         CAST(sum(gross_cents) OVER (PARTITION BY order_id) AS BIGINT) AS order_gross_cents,
         row_number() OVER (PARTITION BY order_id ORDER BY line_no DESC) AS from_last
  FROM lines
),
floors AS (
  SELECT *,
         CAST(floor(CAST(order_discount_cents AS DOUBLE) * gross_cents / order_gross_cents) AS BIGINT) AS discount_floor,
         CAST(floor(CAST(order_shipping_cents AS DOUBLE) * gross_cents / order_gross_cents) AS BIGINT) AS shipping_floor
  FROM shares
),
allocated AS (
  SELECT *,
         discount_floor + CASE WHEN from_last = 1 THEN order_discount_cents - CAST(sum(discount_floor) OVER (PARTITION BY order_id) AS BIGINT) ELSE 0 END AS discount_cents,
         shipping_floor + CASE WHEN from_last = 1 THEN order_shipping_cents - CAST(sum(shipping_floor) OVER (PARTITION BY order_id) AS BIGINT) ELSE 0 END AS shipping_cents
  FROM floors
)
SELECT order_id,
       line_no,
       year(order_date) * 10000 + month(order_date) * 100 + day(order_date) AS date_key,
       customer_id,
       product_id,
       store_id,
       quantity,
       CAST(gross_cents AS DECIMAL(18, 0)) * 0.01 AS gross_amount,
       CAST(discount_cents AS DECIMAL(18, 0)) * 0.01 AS allocated_discount,
       CAST(shipping_cents AS DECIMAL(18, 0)) * 0.01 AS allocated_shipping,
       CAST(gross_cents - discount_cents AS DECIMAL(18, 0)) * 0.01 AS net_amount,
       CAST(gross_cents - discount_cents - cost_cents AS DECIMAL(18, 0)) * 0.01 AS margin_amount
FROM allocated
