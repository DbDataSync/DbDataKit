-- One row per purchase order line: how much of it arrived and was rejected, how long the vendor took, and whether it shipped by the date the line was due.
SELECT l.purchase_order_id,
       l.purchase_order_detail_id,
       o.vendor_id,
       l.product_id,
       o.order_date,
       o.ship_date,
       l.due_date,
       l.order_qty,
       l.received_qty,
       l.rejected_qty,
       CAST(l.line_total AS DECIMAL(19, 4)) AS line_total,
       CAST(round(l.received_qty / l.order_qty * 100, 1) AS DECIMAL(5, 1)) AS received_percent,
       CAST(round(l.rejected_qty / nullif(l.received_qty, 0) * 100, 2) AS DECIMAL(6, 2)) AS rejected_percent,
       date_diff('day', o.order_date, o.ship_date) AS lead_days,
       CASE WHEN o.ship_date IS NULL THEN NULL WHEN o.ship_date <= l.due_date THEN true ELSE false END AS shipped_on_time
FROM staging.purchase_lines l
JOIN staging.purchase_orders o ON o.purchase_order_id = l.purchase_order_id
