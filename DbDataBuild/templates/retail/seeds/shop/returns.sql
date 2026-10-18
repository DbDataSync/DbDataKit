-- About one line in twenty of the delivered orders comes back, 5 to 30 days after the order, for one of the five reasons.
WITH r AS (
  SELECT l.order_id, l.line_no, l.quantity, CAST(o.order_ts AS DATE) AS order_date
  FROM shop.order_lines l
  JOIN shop.orders o ON o.order_id = l.order_id
  WHERE o.status_code = 'DL' AND rnd(l.order_id * 10 + l.line_no, 51) < 0.05
)
SELECT row_number() OVER (ORDER BY order_id, line_no) AS return_id,
       order_id,
       line_no,
       order_date + CAST(5 + pick(order_id * 10 + line_no, 52, 26) AS INTEGER) AS return_date,
       ['SIZE', 'DEFE', 'LATE', 'NOTW', 'CHNG'][1 + pick(order_id * 10 + line_no, 53, 5)] AS reason_code,
       1 + pick(order_id * 10 + line_no, 54, quantity) AS quantity
FROM r
