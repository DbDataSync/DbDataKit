SELECT * FROM (VALUES
  (1, 'Northwest', 'US', 'North America', 'USD'), (2, 'Northeast', 'US', 'North America', 'USD'), (3, 'Central', 'US', 'North America', 'USD'),
  (4, 'Southwest', 'US', 'North America', 'USD'), (5, 'Southeast', 'US', 'North America', 'USD'), (6, 'Canada', 'CA', 'North America', 'CAD'),
  (7, 'France', 'FR', 'Europe', 'EUR'), (8, 'Germany', 'DE', 'Europe', 'EUR'), (9, 'Australia', 'AU', 'Pacific', 'AUD'), (10, 'United Kingdom', 'GB', 'Europe', 'GBP')
) AS t(territory_id, name, country_region_code, group_name, currency_code)
