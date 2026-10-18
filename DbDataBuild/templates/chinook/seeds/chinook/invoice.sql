-- Invoice dates fall over four years, in the order of the invoice number inside each customer's life; the total is the sum of its lines.
SELECT i.invoice_id,
       c.customer_id,
       TIMESTAMP '2021-01-01 00:00:00' + to_days(pick(i.invoice_id, 44, 1461)) + to_seconds(pick(i.invoice_id, 45, 86400)) AS invoice_date,
       c.city AS billing_city,
       c.country AS billing_country,
       CAST(sum(l.unit_price * l.quantity) AS DECIMAL(10, 2)) AS total
FROM (SELECT i AS invoice_id FROM generate_series(1, getvariable('scale') * 6) AS g(i)) i
JOIN chinook.customer c ON c.customer_id = 1 + (i.invoice_id - 1) % getvariable('scale')
JOIN chinook.invoice_line l ON l.invoice_id = i.invoice_id
GROUP BY i.invoice_id, c.customer_id, c.city, c.country
