SELECT orders.ordered_at_month,
       MEASURE(orders.net_revenue)
FROM sales_catalog(timezone := 'Central Time')
GROUP BY ALL
LIMIT 100
