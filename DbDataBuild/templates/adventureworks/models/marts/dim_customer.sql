SELECT c.customer_id,
       c.customer_name,
       c.email,
       substr(c.email, strpos(c.email, '@') + 1) AS email_domain,
       c.customer_type,
       c.store_name,
       t.territory_name,
       t.country_region_code,
       t.territory_group
FROM staging.customers c
JOIN staging.territories t ON t.territory_id = c.territory_id
