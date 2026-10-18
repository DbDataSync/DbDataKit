-- One row per customer with the name of the support agent and the domain of the email, and the segment of the company.
SELECT c.customer_id,
       c.full_name,
       c.company,
       CASE WHEN c.company IS NULL THEN 'consumer' ELSE 'business' END AS customer_type,
       c.city,
       c.state,
       c.country,
       substr(c.email, strpos(c.email, '@') + 1) AS email_domain,
       e.full_name AS support_rep
FROM staging.customers c
JOIN staging.employees e ON e.employee_id = c.support_rep_id
