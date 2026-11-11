SELECT customers.region,
       MEASURE(orders.order_count, rollup := {'date.fiscal_month': 'sum'}, acknowledge := TRUE) AS orders_summed
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
