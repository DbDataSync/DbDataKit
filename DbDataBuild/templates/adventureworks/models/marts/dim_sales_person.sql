-- A sales person is an employee; the territory, the current quota (the latest quarter's), and the bonus terms.
SELECT sp.sales_person_id,
       e.user_name,
       e.job_title,
       t.territory_name,
       sp.sales_quota AS yearly_quota,
       sp.bonus,
       sp.commission_pct
FROM staging.sales_people sp
JOIN staging.employees e ON e.employee_id = sp.sales_person_id
LEFT JOIN staging.territories t ON t.territory_id = sp.territory_id
