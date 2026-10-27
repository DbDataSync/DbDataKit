-- One row for each sale. `sold_on` is a day of the calendar (`marts.dim_date`): the semantic model joins the two on it, and the generated checks say when a sale falls outside the calendar.
SELECT sale_id, sold_on, units, amount
FROM shop.sales
