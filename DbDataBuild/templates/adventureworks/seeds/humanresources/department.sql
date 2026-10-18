SELECT * FROM (VALUES
  (1, 'Engineering', 'Research and Development'), (2, 'Sales', 'Sales and Marketing'), (3, 'Marketing', 'Sales and Marketing'), (4, 'Production', 'Manufacturing'),
  (5, 'Purchasing', 'Inventory Management'), (6, 'Finance', 'Executive General and Administration'), (7, 'Human Resources', 'Executive General and Administration'), (8, 'Quality Assurance', 'Quality Assurance')
) AS t(department_id, name, group_name)
