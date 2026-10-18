-- One row per customer, with the region their country belongs to (a lookup in the reference data, not a rule in the query).
SELECT c.customer_id,
       c.first_name,
       c.last_name,
       c.email_domain,
       c.country_code,
       coalesce(r.region, 'Unknown') AS region,
       c.signup_date,
       year(c.signup_date) * 100 + month(c.signup_date) AS signup_month,
       c.marketing_opt_in
FROM staging.customers c
LEFT JOIN staging.country_regions r ON r.country_code = c.country_code
