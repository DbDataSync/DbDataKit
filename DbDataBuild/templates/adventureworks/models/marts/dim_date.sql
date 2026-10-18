-- A calendar from July 2021 for 1,281 days, with the fiscal year (which starts in July) and its quarter.
WITH days AS (
  SELECT CAST(DATE '2021-07-01' + CAST(n AS INTEGER) AS DATE) AS day FROM generate_series(0, 1280) AS g(n)
)
SELECT year(day) * 10000 + month(day) * 100 + day(day) AS date_key,
       day AS date,
       year(day) AS year,
       quarter(day) AS quarter,
       month(day) AS month,
       year(day) * 100 + month(day) AS year_month,
       CASE month(day) WHEN 1 THEN 'January' WHEN 2 THEN 'February' WHEN 3 THEN 'March' WHEN 4 THEN 'April' WHEN 5 THEN 'May' WHEN 6 THEN 'June'
                       WHEN 7 THEN 'July' WHEN 8 THEN 'August' WHEN 9 THEN 'September' WHEN 10 THEN 'October' WHEN 11 THEN 'November' ELSE 'December' END AS month_name,
       date_part('isodow', day) AS iso_weekday,
       date_part('isodow', day) >= 6 AS is_weekend,
       CASE WHEN month(day) >= 7 THEN year(day) + 1 ELSE year(day) END AS fiscal_year,
       CASE WHEN month(day) >= 7 THEN CAST(floor((month(day) - 7) / 3.0) AS INTEGER) + 1 ELSE CAST(floor((month(day) + 5) / 3.0) AS INTEGER) + 1 END AS fiscal_quarter
FROM days
