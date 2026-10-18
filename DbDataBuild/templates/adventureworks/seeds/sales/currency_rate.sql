-- US dollars for one unit of each currency, on weekdays only (the weekend takes the Friday rate, which is why a query looks up the latest rate on or before a day).
SELECT d AS currency_rate_date,
       c.code AS to_currency_code,
       CAST(round(c.base * (1 + 0.08 * sin(date_diff('day', DATE '2021-12-27', d) / 45.0 + c.phase)), 4) AS DECIMAL(10, 4)) AS average_rate,
       CAST(round(c.base * (1 + 0.08 * sin(date_diff('day', DATE '2021-12-27', d) / 45.0 + c.phase)) * (1 + (rnd(date_diff('day', DATE '2021-12-27', d), 55) - 0.5) * 0.01), 4) AS DECIMAL(10, 4)) AS end_of_day_rate
FROM (SELECT CAST(x AS DATE) AS d FROM generate_series(DATE '2021-12-27', DATE '2024-12-31', INTERVAL 1 DAY) AS s(x)) AS days
CROSS JOIN (VALUES ('CAD', 0.78, 0.0), ('EUR', 1.10, 1.0), ('AUD', 0.70, 2.0), ('GBP', 1.30, 3.0)) AS c(code, base, phase)
WHERE isodow(d) < 6
