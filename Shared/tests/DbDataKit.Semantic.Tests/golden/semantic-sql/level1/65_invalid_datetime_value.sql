SELECT orders.ordered_at_month,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE orders.ordered_at >= 'last tuesday'
GROUP BY ALL
LIMIT 100
