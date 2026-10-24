-- The middle and the spread of what a product's lines bring in: the median, the 90th percentile (interpolated between the two values around it) and the 25th percentile (a value that is in the data).
-- The interpolated ones are taken over DOUBLE and cast back: DuckDB 2.0 interpolates a DECIMAL in a way the lowered query cannot reproduce to the last digit, so a quantile over a DECIMAL is refused.
SELECT product_id,
       count(*) AS lines,
       CAST(median(CAST(revenue_usd AS DOUBLE)) AS DECIMAL(19, 4)) AS median_revenue,
       CAST(quantile_cont(CAST(revenue_usd AS DOUBLE), 0.9) AS DECIMAL(19, 4)) AS p90_revenue,
       quantile_disc(revenue_usd, 0.25) AS p25_revenue,
       min(revenue_usd) AS min_revenue,
       max(revenue_usd) AS max_revenue
FROM marts.fct_sales_lines
GROUP BY product_id
