-- Local demonstration database; run only in a fresh disposable sample container.
CREATE TABLE customers (id integer PRIMARY KEY, name text NOT NULL);
INSERT INTO customers VALUES (1, 'Acme'), (2, 'Nova');
CREATE TABLE private_notes (id integer PRIMARY KEY, note text NOT NULL);
INSERT INTO private_notes VALUES (1, 'Not visible to the agent');

CREATE ROLE sample_reader LOGIN PASSWORD 'local-sample-only';
REVOKE ALL ON DATABASE typesafedb FROM PUBLIC;
GRANT CONNECT ON DATABASE typesafedb TO sample_reader;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO sample_reader;
GRANT SELECT ON customers TO sample_reader;
ALTER ROLE sample_reader SET default_transaction_read_only = on;
ALTER ROLE sample_reader SET statement_timeout = '5s';
