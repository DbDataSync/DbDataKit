-- Recency, frequency and monetary value per customer, each cut into five equal groups (5 is best), and a segment name written as a mapping of the three scores.
WITH per_customer AS (
  SELECT f.customer_id,
         max(d.date) AS last_order_date,
         count(DISTINCT f.sales_order_id) AS orders,
         sum(f.revenue_usd) AS revenue
  FROM marts.fct_sales_lines f
  JOIN marts.dim_date d ON d.date_key = f.date_key
  GROUP BY f.customer_id
),
scored AS (
  SELECT customer_id, last_order_date, orders, revenue,
         date_diff('day', last_order_date, DATE '2025-01-01') AS recency_days,
         ntile(5) OVER (ORDER BY last_order_date, customer_id) AS r_score,
         ntile(5) OVER (ORDER BY orders, customer_id) AS f_score,
         ntile(5) OVER (ORDER BY revenue, customer_id) AS m_score
  FROM per_customer
)
SELECT customer_id,
       recency_days,
       orders,
       CAST(revenue AS DECIMAL(19, 2)) AS revenue,
       r_score, f_score, m_score,
       CASE WHEN r_score >= 4 AND f_score >= 4 THEN 'champion'
            WHEN r_score >= 4 THEN 'recent'
            WHEN f_score >= 4 AND m_score >= 4 THEN 'loyal big spender'
            WHEN r_score <= 2 AND m_score >= 4 THEN 'at risk'
            WHEN r_score <= 2 THEN 'lapsed'
            ELSE 'regular' END AS segment
FROM scored
