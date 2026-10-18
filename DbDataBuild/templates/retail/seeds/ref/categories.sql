SELECT * FROM (VALUES
  ('TENT', 'Tents', 'Camping'), ('STOV', 'Stoves', 'Camping'), ('BAGS', 'Backpacks', 'Packs'),
  ('BOOT', 'Boots', 'Footwear'), ('JACK', 'Jackets', 'Apparel'), ('ACCS', 'Accessories', 'Accessories')
) AS t(category_code, category_name, department)
