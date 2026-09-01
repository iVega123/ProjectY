-- Bootstrap do PostgreSQL: papéis.
--
-- O banco vem de POSTGRES_DB no contêiner (ou de createdb), porque o Postgres
-- não aceita CREATE DATABASE IF NOT EXISTS nem CREATE DATABASE dentro de um
-- bloco transacional.
--
-- Papel também não tem IF NOT EXISTS aqui, então a forma idempotente é capturar
-- a exceção. É feio, e é exatamente por isso que esta parte não mora no schema.

DO $$ BEGIN
    CREATE ROLE rental_core LOGIN;
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
    CREATE ROLE media_guard LOGIN;
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;
