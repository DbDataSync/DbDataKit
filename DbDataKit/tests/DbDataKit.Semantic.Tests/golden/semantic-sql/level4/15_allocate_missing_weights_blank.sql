SELECT customers.region,
       products.product,
       MEASURE(orders.net_revenue),
       MEASURE(budget.budget_amount, spread := {'allocate': {'by': 'measure', 'measure': 'orders.net_revenue', 'fallback': 'blank'}})
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
  AND products.product = 'A'
  AND orders.status = 'Cancelled'
GROUP BY ALL
LIMIT 100
