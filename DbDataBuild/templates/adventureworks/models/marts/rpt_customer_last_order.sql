-- The latest order of every customer. QUALIFY filters on a window function the way HAVING filters on an aggregate.
SELECT customer_id, sales_order_id, order_date, status_name, total_due
FROM staging.orders
QUALIFY row_number() OVER (PARTITION BY customer_id ORDER BY order_date DESC, sales_order_id DESC) = 1
