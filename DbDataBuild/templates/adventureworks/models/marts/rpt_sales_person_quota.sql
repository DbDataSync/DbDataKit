-- Quota attainment: what each sales person sold in a quarter against that quarter's quota, the rank among the people who had one, and where they stand as a percentile.
WITH sold AS (
  SELECT f.sales_person_id, d.year, d.quarter, sum(f.revenue_usd) AS revenue
  FROM marts.fct_sales_lines f
  JOIN marts.dim_date d ON d.date_key = f.date_key
  WHERE f.sales_person_id IS NOT NULL
  GROUP BY f.sales_person_id, d.year, d.quarter
),
quota AS (
  SELECT sales_person_id, year(quota_date) AS year, quarter(quota_date) AS quarter, sales_quota FROM staging.sales_quotas
)
SELECT q.sales_person_id,
       q.year,
       q.quarter,
       q.sales_quota,
       CAST(coalesce(s.revenue, 0) AS DECIMAL(19, 2)) AS revenue,
       CAST(coalesce(s.revenue, 0) / q.sales_quota AS DECIMAL(9, 4)) AS attainment,
       rank() OVER (PARTITION BY q.year, q.quarter ORDER BY coalesce(s.revenue, 0) / q.sales_quota DESC) AS attainment_rank,
       CAST(percent_rank() OVER (PARTITION BY q.year, q.quarter ORDER BY coalesce(s.revenue, 0) / q.sales_quota) AS DECIMAL(9, 4)) AS percentile,
       coalesce(s.revenue, 0) >= q.sales_quota AS met_quota
FROM quota q
LEFT JOIN sold s ON s.sales_person_id = q.sales_person_id AND s.year = q.year AND s.quarter = q.quarter
