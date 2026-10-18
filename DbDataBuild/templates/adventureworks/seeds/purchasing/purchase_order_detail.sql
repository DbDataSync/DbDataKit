-- One to five lines to a purchase order for components and parts. The unit price is within ten percent of the product's current cost; most lines arrive in full, some late-rejected.
WITH picked AS (
  SELECT i AS purchase_order_id, k, 31 + pick(i * 10 + k, 117, 120) AS product_id, 10 + pick(i * 10 + k, 118, 490) AS order_qty
  FROM generate_series(1, 600) AS g(i)
  CROSS JOIN generate_series(1, 5) AS h(k)
  WHERE k <= 1 + pick(i, 119, 5)
)
SELECT p.purchase_order_id,
       row_number() OVER (ORDER BY p.purchase_order_id, p.k) AS purchase_order_detail_id,
       p.product_id,
       CAST(p.order_qty AS SMALLINT) AS order_qty,
       CAST(round(pr.standard_cost * (0.9 + 0.2 * rnd(p.purchase_order_id * 10 + p.k, 120)), 4) AS DECIMAL(19, 4)) AS unit_price,
       CAST(CASE WHEN rnd(p.purchase_order_id * 10 + p.k, 121) < 0.85 THEN p.order_qty ELSE floor(p.order_qty * 0.8) END AS DECIMAL(8, 2)) AS received_qty,
       CAST(CASE WHEN rnd(p.purchase_order_id * 10 + p.k, 122) < 0.15 THEN floor(p.order_qty * 0.05) ELSE 0 END AS DECIMAL(8, 2)) AS rejected_qty,
       purchase_date_of(p.purchase_order_id) + to_days(14 + pick(p.purchase_order_id * 10 + p.k, 123, 17)) AS due_date
FROM picked p
JOIN production.product pr ON pr.product_id = p.product_id
