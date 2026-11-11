SELECT orders.ordered_at_fiscal_month,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE orders.ordered_at BETWEEN '2026-07-01' AND '2026-09-30'
  AND orders.ordered_at_fiscal_month IS NOT NULL
GROUP BY ALL
LIMIT 100
