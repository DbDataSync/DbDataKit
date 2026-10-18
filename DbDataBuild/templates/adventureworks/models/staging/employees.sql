-- The codes for marital status and gender as words; the login is `domain\name`, and the name is what comes after the backslash.
SELECT employee_id,
       login_id,
       substr(login_id, strpos(login_id, '\') + 1) AS user_name,
       job_title,
       birth_date,
       CASE marital_status WHEN 'M' THEN 'married' WHEN 'S' THEN 'single' ELSE 'unknown' END AS marital_status,
       CASE gender WHEN 'M' THEN 'male' WHEN 'F' THEN 'female' ELSE 'unknown' END AS gender,
       hire_date,
       salaried_flag AS is_salaried,
       vacation_hours,
       sick_leave_hours,
       current_flag AS is_current
FROM humanresources.employee
