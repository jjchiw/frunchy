-- Runs once, when the Postgres data volume is created (docker-entrypoint-initdb.d), as the POSTGRES_USER role.
-- POSTGRES_DB already created frunchydb. Megafono (the federation profile) keeps its own database on the same
-- server, owned by the same role: it creates its own schema at startup. Without the profile it stays empty.
CREATE DATABASE megafono;
