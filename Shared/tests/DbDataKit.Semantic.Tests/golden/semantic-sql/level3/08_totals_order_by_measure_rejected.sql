SELECT customers.region,
       MEASURE(orders.net_revenue)
FROM sales_catalog(totals := {'subtotal_by': ['customers.region']})
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
ORDER BY MEASURE(orders.net_revenue) DESC
LIMIT 100
