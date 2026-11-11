SELECT date.fiscal_month,
       MEASURE(orders.customer_count),
       "window"(MEASURE(orders.customer_count), along := 'date.fiscal_month', start := -2, "end" := 'current') AS customers_r3
FROM sales_catalog
GROUP BY ALL
LIMIT 100
