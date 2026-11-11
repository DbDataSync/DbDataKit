SELECT orders.ordered_at_fiscal_month,
       MEASURE(orders.net_revenue),
       "window"(MEASURE(orders.net_revenue), along := 'orders.ordered_at_fiscal_month', start := {'period_start': 'orders.ordered_at_fiscal_year'}, "end" := 'current') AS ytd
FROM sales_catalog
WHERE orders.ordered_at_fiscal_year = 'FY27'
GROUP BY ALL
LIMIT 100
