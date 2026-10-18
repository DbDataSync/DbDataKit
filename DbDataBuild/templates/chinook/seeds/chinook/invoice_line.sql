-- Six invoices to a customer, one to five lines on each, a track chosen by hash; prices come from the catalog.
WITH picked AS (
  SELECT i AS invoice_id, k, 1 + pick(i * 10 + k, 41, 1200) AS track_id, CASE WHEN rnd(i * 10 + k, 42) < 0.9 THEN 1 ELSE 2 END AS quantity
  FROM generate_series(1, getvariable('scale') * 6) AS g(i)
  CROSS JOIN generate_series(1, 5) AS h(k)
  WHERE k <= 1 + pick(i, 43, 5)
)
SELECT row_number() OVER (ORDER BY p.invoice_id, p.k) AS invoice_line_id,
       p.invoice_id,
       p.track_id,
       t.unit_price,
       p.quantity
FROM picked p
JOIN chinook.track t ON t.track_id = p.track_id
