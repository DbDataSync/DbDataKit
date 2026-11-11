SELECT customers.region,
       products.product,
       MEASURE(budget.budget_amount, spread := {'allocate': {'by': 'measure', 'measure': 'orders.shipped_revenue', 'period': {'unit': 'fiscal_year', 'offset': -1}}})
FROM sales_catalog
WHERE date.fiscal_month = '2026-08'
  AND products.product = 'A'
GROUP BY ALL
LIMIT 100
