-- Each order is paid in one or two payments (a second one 30% of the time) by different methods; the order's
-- amount is split 70/30 when there are two.
WITH total AS (
  SELECT id AS order_id, 500 + pick(id, 6, 4500) AS cents FROM raw.orders
),
p AS (
  SELECT t.order_id, n.n AS n, t.cents, rnd(t.order_id, 7) < 0.3 AS split
  FROM total t
  JOIN range(1, 3) AS n(n) ON n.n = 1 OR rnd(t.order_id, 7) < 0.3
)
SELECT order_id * 10 + n AS id,
       order_id,
       ['credit_card', 'bank_transfer', 'coupon', 'gift_card'][1 + pick(order_id * 10 + n, 8, 4)] AS payment_method,
       CASE WHEN NOT split THEN cents WHEN n = 1 THEN CAST(cents * 0.7 AS INTEGER) ELSE cents - CAST(cents * 0.7 AS INTEGER) END AS amount_cents
FROM p
