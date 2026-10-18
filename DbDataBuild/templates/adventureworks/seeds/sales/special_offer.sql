SELECT * FROM (VALUES
  (1, 'No Discount', 0.00, 'No Discount', 'No Discount', DATE '2019-01-01', DATE '2099-12-31', 0, NULL),
  (2, 'Volume Discount 11 to 14', 0.02, 'Volume Discount', 'Reseller', DATE '2019-01-01', DATE '2099-12-31', 11, 14),
  (3, 'Volume Discount 15 to 24', 0.05, 'Volume Discount', 'Reseller', DATE '2019-01-01', DATE '2099-12-31', 15, 24),
  (4, 'Mountain-100 Clearance Sale', 0.35, 'Discontinued Product', 'Reseller', DATE '2023-05-01', DATE '2023-06-30', 0, NULL),
  (5, 'Spring Promotion', 0.10, 'Seasonal Discount', 'Customer', DATE '2022-03-01', DATE '2022-05-31', 0, NULL),
  (6, 'Holiday Special', 0.15, 'Seasonal Discount', 'Customer', DATE '2023-11-15', DATE '2023-12-31', 0, NULL)
) AS t(special_offer_id, description, discount_pct, offer_type, category, start_date, end_date, min_qty, max_qty)
