SELECT date.fiscal_month,
       MEASURE(orders.customer_count),
       "window"(MEASURE(orders.customer_count), along := 'date.fiscal_month', "offset" := -1, output := 'delta') AS change
FROM sales_catalog
GROUP BY ALL
LIMIT 100
