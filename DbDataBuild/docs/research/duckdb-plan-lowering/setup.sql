CREATE SCHEMA staging;
CREATE TABLE staging.cities (country VARCHAR, name VARCHAR, year INTEGER, population BIGINT);
INSERT INTO staging.cities VALUES ('NL','Amsterdam',2000,1005),('NL','Amsterdam',2010,1065),('NL','Amsterdam',2020,1158),('US','Seattle',2000,564),('US','Seattle',2010,608),('US','Seattle',2020,738),('US','NYC',2000,8015),('US','NYC',2010,8175),('US','NYC',2020,8772);
CREATE TABLE staging.wide (id INTEGER, jan INTEGER, feb INTEGER, mar INTEGER, note VARCHAR);
CREATE MACRO net(a, tax := 0.2) AS a * (1 + tax);
CREATE MACRO big_cities(min_pop) AS TABLE SELECT * FROM staging.cities WHERE population > min_pop;
CREATE TYPE yr AS ENUM ('2000','2010','2020');
