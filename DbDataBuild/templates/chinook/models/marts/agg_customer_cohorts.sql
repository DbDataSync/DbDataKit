-- Customers grouped by the month of their first purchase; for every later month, how many of the cohort bought again.
WITH firsts AS (
  SELECT customer_id, min(date_trunc('month', i.invoice_date)) AS cohort_month
  FROM staging.invoices i
  GROUP BY customer_id
),
activity AS (
  SELECT DISTINCT i.customer_id, date_trunc('month', i.invoice_date) AS active_month
  FROM staging.invoices i
)
SELECT year(f.cohort_month) * 100 + month(f.cohort_month) AS cohort,
       date_diff('month', f.cohort_month, a.active_month) AS months_since_first,
       count(*) AS active_customers
FROM firsts f
JOIN activity a ON a.customer_id = f.customer_id
GROUP BY year(f.cohort_month) * 100 + month(f.cohort_month), date_diff('month', f.cohort_month, a.active_month)
