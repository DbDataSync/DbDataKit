-- One row per work order: the scrap rate, how many days it took, and how many days past its due date it finished (an order with no end date is still in progress).
SELECT w.work_order_id,
       w.product_id,
       w.order_qty,
       w.scrapped_qty,
       CAST(w.scrapped_qty / CAST(w.order_qty AS DOUBLE) AS DECIMAL(9, 4)) AS scrap_rate,
       w.start_date,
       w.end_date,
       w.due_date,
       date_diff('day', w.start_date, w.end_date) AS days_to_complete,
       greatest(0, date_diff('day', w.due_date, w.end_date)) AS days_late,
       CASE WHEN w.end_date IS NULL THEN 'in progress' WHEN w.end_date <= w.due_date THEN 'on time' ELSE 'late' END AS delivery
FROM staging.work_orders w
