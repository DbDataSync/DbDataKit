SELECT customers.region,
       date.fiscal_month,
       MEASURE(orders.net_revenue)
FROM sales_catalog(totals := {'subtotal_by': ['date.fiscal_month'], 'grand_total': FALSE})
WHERE date.calendar_date BETWEEN '2026-07-01' AND '2026-08-31'
GROUP BY ALL
HAVING MEASURE(orders.net_revenue) >= 150
LIMIT 100
