SELECT customer_id,
       first_name || ' ' || last_name AS full_name,
       company,
       city,
       state,
       country,
       postal_code,
       phone,
       email,
       support_rep_id
FROM chinook.customer
