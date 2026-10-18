-- The orders the lines belong to, placed through the website (40%), the phone (10%) or a shop, over 2024. Some carry a discount of 5 to 20 percent
-- of the goods, and every order a shipping fee that grows with its weight.
WITH o AS (
  SELECT l.order_id,
         sum(l.quantity * l.unit_price) AS goods,
         sum(l.quantity * p.weight_grams) AS grams
  FROM shop.order_lines l
  JOIN shop.products p ON p.product_id = l.product_id
  GROUP BY l.order_id
)
SELECT o.order_id,
       o.order_id // 100 AS customer_id,
       CASE WHEN rnd(o.order_id, 31) < 0.40 THEN 1
            WHEN rnd(o.order_id, 31) < 0.50 THEN 2
            ELSE 3 + pick(o.order_id, 32, 10) END AS store_id,
       TIMESTAMP '2024-01-01 00:00:00' + to_days(pick(o.order_id, 33, 366)) + to_seconds(pick(o.order_id, 34, 86400)) AS order_ts,
       CASE WHEN rnd(o.order_id, 35) < 0.85 THEN 'DL' WHEN rnd(o.order_id, 35) < 0.93 THEN 'SH' WHEN rnd(o.order_id, 35) < 0.97 THEN 'OP' ELSE 'CX' END AS status_code,
       CAST(CASE WHEN rnd(o.order_id, 36) < 0.4 THEN round(o.goods * (0.05 + pick(o.order_id, 37, 4) * 0.05), 2) ELSE 0 END AS DECIMAL(10, 2)) AS discount_amount,
       CAST(round(4.95 + o.grams / 1000.0 * 0.6, 2) AS DECIMAL(10, 2)) AS shipping_fee,
       CASE WHEN rnd(o.order_id, 38) < 0.15 THEN 'WELCOME10' END AS coupon_code
FROM o
