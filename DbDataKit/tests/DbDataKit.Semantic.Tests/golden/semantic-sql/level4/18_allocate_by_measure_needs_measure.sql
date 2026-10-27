SELECT customers.region,
       products.product,
       MEASURE(orders.net_revenue),
       MEASURE(budget.budget_amount, spread := {'allocate': {'by': 'measure'}})
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
