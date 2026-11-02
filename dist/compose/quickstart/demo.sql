-- The demo database the QuickStart starts with: a small sales schema to point Sync, Proxy, Build and Store at.
CREATE TABLE customers (id serial PRIMARY KEY, name text NOT NULL, country text NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE orders (id serial PRIMARY KEY, customer_id int NOT NULL REFERENCES customers (id), total numeric(10,2) NOT NULL, placed_at timestamptz NOT NULL DEFAULT now());
INSERT INTO customers (name, country) VALUES ('Ada', 'UK'), ('Grace', 'US'), ('Linus', 'FI'), ('Margaret', 'US');
INSERT INTO orders (customer_id, total) VALUES (1, 120.50), (1, 80.00), (2, 310.25), (3, 45.99), (4, 999.00);
-- a second database for Sync to replicate into, and one for the Store's master data
CREATE DATABASE demo_target;
CREATE DATABASE mdm;
