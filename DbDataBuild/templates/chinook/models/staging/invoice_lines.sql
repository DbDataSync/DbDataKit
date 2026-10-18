SELECT invoice_line_id,
       invoice_id,
       track_id,
       unit_price,
       quantity,
       unit_price * quantity AS line_amount
FROM chinook.invoice_line
