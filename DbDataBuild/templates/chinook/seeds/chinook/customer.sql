-- `scale` customers from ten countries; a few have a company, a state or no phone.
WITH c AS (
  SELECT i AS customer_id,
         [('USA','New York','NY','10001'),('USA','Austin','TX','73301'),('Canada','Toronto','ON','M5V'),('Brazil','Sao Paulo',NULL,'01000'),('France','Paris',NULL,'75001'),
          ('Germany','Berlin',NULL,'10115'),('United Kingdom','London',NULL,'EC1A'),('Australia','Sydney','NSW','2000'),('India','Mumbai','MH','400001'),('Norway','Oslo',NULL,'0150')][1 + pick(i, 31, 10)] AS place
  FROM generate_series(1, getvariable('scale')) AS g(i)
)
SELECT customer_id,
       ['Ada','Ben','Chen','Dara','Eli','Fatima','Gus','Hana','Ivan','Jun','Kofi','Lena','Marco','Nia','Omar','Pia'][1 + pick(customer_id, 32, 16)] AS first_name,
       ['Almeida','Brandt','Chowdhury','Dubois','Eriksen','Fischer','Garcia','Hughes','Ito','Jones','Kowalski','Lopez','Meyer','Nguyen'][1 + pick(customer_id, 33, 14)] AS last_name,
       CASE WHEN rnd(customer_id, 34) < 0.2 THEN ['Acme Audio','Bluebird Media','Cascade Labs','Delta Sound'][1 + pick(customer_id, 35, 4)] END AS company,
       place[2] AS city,
       place[3] AS state,
       place[1] AS country,
       place[4] AS postal_code,
       CASE WHEN rnd(customer_id, 36) < 0.9 THEN '+' || (100 + pick(customer_id, 37, 800)) || ' ' || (1000000 + pick(customer_id, 38, 8999999)) END AS phone,
       'customer' || customer_id || '@mail.example' AS email,
       3 + pick(customer_id, 39, 3) AS support_rep_id
FROM c
