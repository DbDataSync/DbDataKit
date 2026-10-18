-- The calendar: one row for every day the shop, the warehouse or the returns desk did something. Day and month names are a mapping written out here,
-- because the engines name them differently (and in their own language).
WITH days AS (
  SELECT order_date AS day FROM staging.orders
  UNION
  SELECT shipped_date FROM staging.shipments
  UNION
  SELECT return_date FROM staging.returns
)
SELECT year(day) * 10000 + month(day) * 100 + day(day) AS date_key,
       day AS date,
       year(day) AS year,
       quarter(day) AS quarter,
       month(day) AS month,
       year(day) * 100 + month(day) AS year_month,
       CASE month(day) WHEN 1 THEN 'Jan' WHEN 2 THEN 'Feb' WHEN 3 THEN 'Mar' WHEN 4 THEN 'Apr' WHEN 5 THEN 'May' WHEN 6 THEN 'Jun'
                       WHEN 7 THEN 'Jul' WHEN 8 THEN 'Aug' WHEN 9 THEN 'Sep' WHEN 10 THEN 'Oct' WHEN 11 THEN 'Nov' ELSE 'Dec' END AS month_name,
       date_part('isodow', day) AS iso_weekday,
       CASE date_part('isodow', day) WHEN 1 THEN 'Mon' WHEN 2 THEN 'Tue' WHEN 3 THEN 'Wed' WHEN 4 THEN 'Thu' WHEN 5 THEN 'Fri' WHEN 6 THEN 'Sat' ELSE 'Sun' END AS weekday_name,
       date_part('isodow', day) >= 6 AS is_weekend
FROM days
