-- A website, a call centre and ten shops. Only the shops have a floor area.
SELECT * FROM (VALUES
  (1, 'Northstar Online', 'US', 'WEB', NULL, DATE '2018-03-01'),
  (2, 'Northstar Phone Orders', 'US', 'PHN', NULL, DATE '2018-03-01'),
  (3, 'Seattle Flagship', 'US', 'STO', 3800, DATE '2018-05-12'), (4, 'Denver', 'US', 'STO', 2400, DATE '2019-04-02'),
  (5, 'Boston', 'US', 'STO', 1500, DATE '2020-09-18'), (6, 'Vancouver', 'CA', 'STO', 2900, DATE '2019-06-30'),
  (7, 'London', 'GB', 'STO', 1800, DATE '2020-02-14'), (8, 'Munich', 'DE', 'STO', 2100, DATE '2021-05-21'),
  (9, 'Paris', 'FR', 'STO', 1200, DATE '2021-10-08'), (10, 'Sydney', 'AU', 'STO', 2600, DATE '2019-11-23'),
  (11, 'Tokyo', 'JP', 'STO', 1700, DATE '2022-03-04'), (12, 'Sao Paulo', 'BR', 'STO', 2200, DATE '2022-08-19')
) AS t(store_id, store_name, country_code, channel_code, floor_area_m2, opened_date)
