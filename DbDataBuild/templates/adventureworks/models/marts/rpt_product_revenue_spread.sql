-- The middle and the spread of what a product's lines bring in: the median, the 90th percentile (interpolated between the two values around it) and the 25th percentile (a value that is in the data).
SELECT product_id,
       count(*) AS lines,
       median(revenue_usd) AS median_revenue,
       quantile_cont(revenue_usd, 0.9) AS p90_revenue,
       quantile_disc(revenue_usd, 0.25) AS p25_revenue,
       min(revenue_usd) AS min_revenue,
       max(revenue_usd) AS max_revenue
FROM marts.fct_sales_lines
GROUP BY product_id
