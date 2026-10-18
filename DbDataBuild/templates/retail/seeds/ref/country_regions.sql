SELECT * FROM (VALUES
  ('US', 'United States', 'North America', 'USD'), ('CA', 'Canada', 'North America', 'CAD'),
  ('GB', 'United Kingdom', 'Europe', 'GBP'), ('DE', 'Germany', 'Europe', 'EUR'), ('FR', 'France', 'Europe', 'EUR'),
  ('AU', 'Australia', 'Asia Pacific', 'AUD'), ('JP', 'Japan', 'Asia Pacific', 'JPY'), ('BR', 'Brazil', 'Latin America', 'BRL')
) AS t(country_code, country_name, region, currency)
