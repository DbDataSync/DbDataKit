SELECT customers.region,
       MEASURE(orders.net_revenue, rollup := {'date.fiscal_month': 'avg'}) AS avg_monthly_revenue
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
