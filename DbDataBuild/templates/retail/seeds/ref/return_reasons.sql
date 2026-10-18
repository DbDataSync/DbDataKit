SELECT * FROM (VALUES
  ('SIZE', 'Wrong size', 'fit', false), ('DEFE', 'Defective', 'quality', true), ('LATE', 'Arrived late', 'shipping', true),
  ('NOTW', 'Not as described', 'quality', true), ('CHNG', 'Changed mind', 'customer', false)
) AS t(reason_code, reason_name, reason_group, is_our_fault)
