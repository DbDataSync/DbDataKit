-- One row per invoice line, loaded incrementally by the line number.
SELECT l.invoice_line_id,
       l.invoice_id,
       year(i.invoice_date) * 10000 + month(i.invoice_date) * 100 + day(i.invoice_date) AS date_key,
       i.customer_id,
       l.track_id,
       l.quantity,
       l.unit_price,
       l.line_amount
FROM staging.invoice_lines l
JOIN staging.invoices i ON i.invoice_id = l.invoice_id
