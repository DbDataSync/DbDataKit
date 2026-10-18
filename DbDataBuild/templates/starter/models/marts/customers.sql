-- One row per customer: how many orders, when, and the lifetime value. Customers who never ordered stay, with zeros.
SELECT c.customer_id,
       c.first_name,
       c.last_name,
       count(o.order_id) AS number_of_orders,
       min(o.order_date) AS first_order_date,
       max(o.order_date) AS most_recent_order_date,
       CAST(coalesce(sum(o.amount), 0) AS DECIMAL(14, 2)) AS lifetime_value
FROM staging.customers c
LEFT JOIN marts.orders o ON o.customer_id = c.customer_id
GROUP BY ALL
