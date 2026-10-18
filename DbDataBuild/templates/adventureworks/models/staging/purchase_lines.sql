SELECT purchase_order_id,
       purchase_order_detail_id,
       product_id,
       order_qty,
       unit_price,
       received_qty,
       rejected_qty,
       due_date,
       order_qty * unit_price AS line_total
FROM purchasing.purchase_order_detail
