-- Sales AGGREGATED to a day, a region and a channel group, with the running total of net sales within each region.
SELECT l.date_key,
       s.region,
       s.channel_group,
       count(DISTINCT l.order_id) AS orders,
       CAST(sum(l.quantity) AS BIGINT) AS units,
       CAST(sum(l.gross_amount) AS DECIMAL(18, 2)) AS gross_amount,
       CAST(sum(l.net_amount) AS DECIMAL(18, 2)) AS net_amount,
       CAST(sum(l.margin_amount) AS DECIMAL(18, 2)) AS margin_amount,
       CAST(sum(sum(l.net_amount)) OVER (PARTITION BY s.region ORDER BY l.date_key) AS DECIMAL(18, 2)) AS region_net_to_date
FROM marts.fct_sales_lines l
JOIN marts.dim_store s ON s.store_id = l.store_id
GROUP BY l.date_key, s.region, s.channel_group
