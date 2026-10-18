-- The status number as words, the online flag as a boolean, and the total the customer owes.
SELECT sales_order_id,
       order_date,
       due_date,
       ship_date,
       status,
       CASE status WHEN 1 THEN 'in process' WHEN 2 THEN 'approved' WHEN 3 THEN 'backordered' WHEN 4 THEN 'rejected' WHEN 5 THEN 'shipped' WHEN 6 THEN 'cancelled' ELSE 'unknown' END AS status_name,
       online_order_flag AS is_online,
       customer_id,
       sales_person_id,
       territory_id,
       currency_code,
       sub_total,
       tax_amt,
       freight,
       sub_total + tax_amt + freight AS total_due
FROM sales.sales_order_header
