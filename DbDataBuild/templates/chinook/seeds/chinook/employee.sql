SELECT * FROM (VALUES
  (1, 'Adams', 'Andrew', 'General Manager', NULL, DATE '1962-02-18', DATE '2002-08-14', 'Edmonton', 'Canada', 'andrew@chinook.example'),
  (2, 'Edwards', 'Nancy', 'Sales Manager', 1, DATE '1958-12-08', DATE '2002-05-01', 'Calgary', 'Canada', 'nancy@chinook.example'),
  (3, 'Peacock', 'Jane', 'Sales Support Agent', 2, DATE '1973-08-29', DATE '2002-04-01', 'Calgary', 'Canada', 'jane@chinook.example'),
  (4, 'Park', 'Margaret', 'Sales Support Agent', 2, DATE '1947-09-19', DATE '2003-05-03', 'Calgary', 'Canada', 'margaret@chinook.example'),
  (5, 'Johnson', 'Steve', 'Sales Support Agent', 2, DATE '1965-03-03', DATE '2003-10-17', 'Calgary', 'Canada', 'steve@chinook.example'),
  (6, 'Mitchell', 'Michael', 'IT Manager', 1, DATE '1973-07-01', DATE '2003-10-17', 'Calgary', 'Canada', 'michael@chinook.example'),
  (7, 'King', 'Robert', 'IT Staff', 6, DATE '1970-05-29', DATE '2004-01-02', 'Lethbridge', 'Canada', 'robert@chinook.example'),
  (8, 'Callahan', 'Laura', 'IT Staff', 6, DATE '1968-01-09', DATE '2004-03-04', 'Lethbridge', 'Canada', 'laura@chinook.example')
) AS t(employee_id, last_name, first_name, title, reports_to, birth_date, hire_date, city, country, email)
