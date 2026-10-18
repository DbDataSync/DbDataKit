-- One to four lines per order (orders are numbered customer_id * 100 + n, one to five per customer), at the product's price.
WITH o AS (
  SELECT c.customer_id * 100 + n.n AS order_id
  FROM shop.customers c
  JOIN range(1, 6) AS n(n) ON n.n <= 1 + pick(c.customer_id, 21, 5)
),
l AS (
  SELECT o.order_id, k.k AS line_no
  FROM o
  JOIN range(1, 5) AS k(k) ON k.k <= 1 + pick(o.order_id, 22, 4)
)
SELECT l.order_id,
       l.line_no,
       p.product_id,
       1 + pick(l.order_id * 10 + l.line_no, 24, 3) AS quantity,
       p.unit_price
FROM l
JOIN shop.products p ON p.product_id = 1 + pick(l.order_id * 10 + l.line_no, 23, 60)
