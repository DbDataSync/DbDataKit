SELECT customer_id,
       first_name || ' ' || last_name AS customer_name,
       email,
       territory_id,
       store_name,
       CASE WHEN store_name IS NULL THEN 'individual' ELSE 'store' END AS customer_type
FROM sales.customer
