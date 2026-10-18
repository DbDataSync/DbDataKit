SELECT invoice_id,
       customer_id,
       invoice_date AS invoiced_at,
       CAST(invoice_date AS DATE) AS invoice_date,
       billing_city,
       billing_country,
       total
FROM chinook.invoice
