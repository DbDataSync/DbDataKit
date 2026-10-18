-- A scorecard per vendor: spend, how often it shipped on time, how long it takes, how much of what it sent was rejected, and its rank by spend among the active vendors.
SELECT v.vendor_id,
       v.vendor_name,
       v.credit_rating,
       v.is_preferred,
       count(*) AS lines,
       CAST(sum(l.line_total) AS DECIMAL(19, 2)) AS spend,
       CAST(round(count(*) FILTER (WHERE l.shipped_on_time) * 100.0 / count(l.shipped_on_time), 1) AS DECIMAL(5, 1)) AS on_time_percent,
       CAST(avg(l.lead_days) AS DECIMAL(9, 1)) AS average_lead_days,
       CAST(sum(l.rejected_qty) AS DECIMAL(12, 2)) AS rejected_units,
       rank() OVER (ORDER BY sum(l.line_total) DESC) AS spend_rank
FROM marts.fct_purchase_lines l
JOIN staging.vendors v ON v.vendor_id = l.vendor_id
WHERE v.is_active
GROUP BY v.vendor_id, v.vendor_name, v.credit_rating, v.is_preferred
