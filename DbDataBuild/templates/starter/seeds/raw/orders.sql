-- One to four orders per customer, spread over 2024. Most are completed or shipped; a few come back.
WITH k AS (
  SELECT c.id AS user_id, n.n AS n, c.id * 10 + n.n AS id
  FROM raw.customers c
  JOIN range(1, 5) AS n(n) ON n.n <= 1 + pick(c.id, 3, 4)
)
SELECT id,
       user_id,
       DATE '2024-01-01' + CAST(pick(id, 4, 365) AS INTEGER) AS order_date,
       CASE WHEN rnd(id, 5) < 0.55 THEN 'completed'
            WHEN rnd(id, 5) < 0.75 THEN 'shipped'
            WHEN rnd(id, 5) < 0.90 THEN 'placed'
            WHEN rnd(id, 5) < 0.96 THEN 'returned'
            ELSE 'return_pending' END AS status
FROM k
