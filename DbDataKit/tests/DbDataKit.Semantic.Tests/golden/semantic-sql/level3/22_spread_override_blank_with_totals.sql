SELECT customers.region,
       products.product,
       MEASURE(orders.net_revenue),
       MEASURE(budget.budget_amount, spread := 'blank')
FROM sales_catalog(totals := {'subtotal_by': ['products.product']})
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
