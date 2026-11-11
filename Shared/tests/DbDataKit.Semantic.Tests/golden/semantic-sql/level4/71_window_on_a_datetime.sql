SELECT orders.ordered_at_fiscal_month,
       MEASURE(orders.net_revenue),
       "window"(MEASURE(orders.net_revenue), along := 'orders.ordered_at_fiscal_month', start := -1, "end" := 'current') AS r2
FROM sales_catalog
WHERE orders.ordered_at_fiscal_month IN ('2026-08', '2026-09')
GROUP BY ALL
LIMIT 100
