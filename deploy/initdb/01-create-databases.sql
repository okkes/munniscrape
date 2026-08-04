-- Runs once, on the first start of an empty Postgres data directory.
--
-- Each product gets a separate database and a separate role, and no role
-- may connect to another's database. That is the whole point of the split:
-- a breach of the shopping connector must not reach bank sessions, and a
-- leaked bank connection string must not read anyone's receipts - or, now,
-- what a credit register says they owe.
-- Separate INSTANCES are stronger still and are where the prod stacks go
-- when the two services stop sharing a host; this is the one-host shape,
-- and it must not be weaker than it looks.
--
-- Passwords arrive as environment variables on the postgres container
-- rather than as literals in this file, so a copy of the repo is never a
-- copy of the credentials.

\getenv bank_password BANK_DB_PASSWORD
\getenv shop_password SHOP_DB_PASSWORD
\getenv registry_password REGISTRY_DB_PASSWORD

-- \getenv leaves the variable undefined when the environment variable is
-- absent, which would make the CREATE ROLE below silently ambiguous.
-- Normalise to empty, then refuse loudly.
\if :{?bank_password}
\else
\set bank_password ''
\endif
\if :{?shop_password}
\else
\set shop_password ''
\endif
\if :{?registry_password}
\else
\set registry_password ''
\endif

-- psql substitutes :'name' almost everywhere, but NOT inside a dollar-quoted
-- string: those are handed to the server byte for byte. This guard used to
-- live inside the DO block below, so the server received a literal
-- `:'bank_password'` and stopped at the colon:
--
--   psql:/docker-entrypoint-initdb.d/01-create-databases.sql:38:
--       ERROR:  syntax error at or near ":"
--
-- With ON_ERROR_STOP set by the entrypoint, that aborted the whole script
-- before either CREATE ROLE ran - and the failure then arrived at the other
-- end of the stack as `28P01 password authentication failed for user
-- "shop_connector"`, because Postgres deliberately reports an unknown role
-- and a wrong password identically. A refusal that reads as a bad password
-- is worse than no refusal at all.
--
-- So: compute the check out here, where substitution works, and raise inside
-- a block that mentions no variable at all.
SELECT
    length(:'bank_password') = 0 AS bank_password_missing,
    length(:'shop_password') = 0 AS shop_password_missing,
    length(:'registry_password') = 0 AS registry_password_missing
\gset

\if :bank_password_missing
DO $$ BEGIN RAISE EXCEPTION 'BANK_DB_PASSWORD is not set on the postgres container'; END $$;
\endif

\if :shop_password_missing
DO $$ BEGIN RAISE EXCEPTION 'SHOP_DB_PASSWORD is not set on the postgres container'; END $$;
\endif

\if :registry_password_missing
DO $$ BEGIN RAISE EXCEPTION 'REGISTRY_DB_PASSWORD is not set on the postgres container'; END $$;
\endif

CREATE ROLE bank_connector WITH LOGIN PASSWORD :'bank_password';
CREATE ROLE shop_connector WITH LOGIN PASSWORD :'shop_password';
CREATE ROLE registry_connector WITH LOGIN PASSWORD :'registry_password';

CREATE DATABASE bank_connector OWNER bank_connector;
CREATE DATABASE shop_connector OWNER shop_connector;
CREATE DATABASE registry_connector OWNER registry_connector;

-- PUBLIC gets CONNECT on a new database by default, which would leave each
-- service one connection string away from the other's data.
REVOKE CONNECT ON DATABASE bank_connector FROM PUBLIC;
REVOKE CONNECT ON DATABASE shop_connector FROM PUBLIC;
REVOKE CONNECT ON DATABASE registry_connector FROM PUBLIC;
GRANT CONNECT ON DATABASE bank_connector TO bank_connector;
GRANT CONNECT ON DATABASE shop_connector TO shop_connector;
GRANT CONNECT ON DATABASE registry_connector TO registry_connector;

-- Same reasoning inside each database. Each service migrates its own
-- schema as its own owner and has no business in the other's.
\connect bank_connector
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT ALL ON SCHEMA public TO bank_connector;

\connect shop_connector
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT ALL ON SCHEMA public TO shop_connector;

\connect registry_connector
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT ALL ON SCHEMA public TO registry_connector;
