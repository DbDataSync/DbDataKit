-- Every day from the first invoice to the last, from a series of numbers counted forward from the first day (not from the days something happened).
WITH bounds AS (
  SELECT min(invoice_date) AS first_day, max(invoice_date) AS last_day FROM staging.invoices
),
days AS (
  SELECT CAST(first_day + CAST(n AS INTEGER) AS DATE) AS day
  FROM bounds
  CROSS JOIN generate_series(0, 3000) AS g(n)
  WHERE n <= date_diff('day', first_day, last_day)
)
SELECT year(day) * 10000 + month(day) * 100 + day(day) AS date_key,
       day AS date,
       year(day) AS year,
       quarter(day) AS quarter,
       month(day) AS month,
       year(day) * 100 + month(day) AS year_month,
       date_trunc('month', day) AS month_start,
       date_part('isodow', day) AS iso_weekday,
       date_part('isodow', day) >= 6 AS is_weekend
FROM days
