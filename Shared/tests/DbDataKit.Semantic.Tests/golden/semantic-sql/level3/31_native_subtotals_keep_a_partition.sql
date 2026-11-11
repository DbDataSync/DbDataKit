SELECT customers.region,
       products.product,
       MEASURE(orders.net_revenue),
       MEASURE(orders.order_count)
FROM sales_catalog(totals := {'subtotal_by': ['products.product'], 'grand_total': FALSE})
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
