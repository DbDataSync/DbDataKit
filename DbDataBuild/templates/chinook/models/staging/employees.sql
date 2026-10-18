SELECT employee_id,
       first_name || ' ' || last_name AS full_name,
       title,
       reports_to,
       hire_date,
       city,
       country,
       email
FROM chinook.employee
