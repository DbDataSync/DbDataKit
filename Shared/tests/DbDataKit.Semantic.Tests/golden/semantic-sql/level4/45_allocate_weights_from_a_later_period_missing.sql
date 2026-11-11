SELECT customers.region,
       products.product,
       MEASURE(orders.net_revenue),
       MEASURE(budget.budget_amount, spread := {'allocate': {'by': 'measure', 'measure': 'orders.net_revenue', 'period': {'unit': 'fiscal_year', 'offset': 1}}})
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
