-- Staging for orders, with the one piece of business knowledge staging may hold: which statuses count as what.
SELECT id AS order_id,
       user_id AS customer_id,
       order_date,
       status,
       CASE WHEN status IN ('completed', 'shipped') THEN 'fulfilled'
            WHEN status IN ('returned', 'return_pending') THEN 'returned'
            ELSE 'open' END AS status_group
FROM raw.orders
