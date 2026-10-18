-- One row per returned line: what was refunded, and which reason group the code belongs to (a lookup in the reference data).
SELECT r.return_id,
       r.order_id,
       r.line_no,
       year(r.return_date) * 10000 + month(r.return_date) * 100 + day(r.return_date) AS date_key,
       l.product_id,
       r.quantity,
       r.quantity * l.unit_price AS refund_amount,
       r.reason_code,
       coalesce(x.reason_group, 'other') AS reason_group,
       coalesce(x.is_our_fault, false) AS is_our_fault
FROM staging.returns r
JOIN staging.order_lines l ON l.order_id = r.order_id AND l.line_no = r.line_no
LEFT JOIN staging.return_reasons x ON x.reason_code = r.reason_code
