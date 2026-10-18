-- One row per customer who bought something: what they spent, how often, how recently, a segment from a mapping of net spend to a name, and their rank.
WITH per_order AS (
  SELECT customer_id, order_id, min(date_key) AS date_key, sum(net_amount) AS net_amount
  FROM marts.fct_sales_lines
  GROUP BY customer_id, order_id
),
per_customer AS (
  SELECT customer_id,
         count(*) AS orders,
         sum(net_amount) AS net_amount,
         min(date_key) AS first_date_key,
         max(date_key) AS last_date_key
  FROM per_order
  GROUP BY customer_id
)
SELECT p.customer_id,
       c.region,
       p.orders,
       CAST(p.net_amount AS DECIMAL(18, 2)) AS net_amount,
       CAST(p.net_amount / p.orders AS DECIMAL(18, 2)) AS average_order_value,
       p.first_date_key,
       p.last_date_key,
       CASE WHEN p.net_amount >= 1500 THEN 'platinum' WHEN p.net_amount >= 700 THEN 'gold' WHEN p.net_amount >= 250 THEN 'silver' ELSE 'bronze' END AS segment,
       rank() OVER (ORDER BY p.net_amount DESC) AS revenue_rank
FROM per_customer p
JOIN marts.dim_customer c ON c.customer_id = p.customer_id
