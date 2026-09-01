-- Bootstrap do CockroachDB: banco e papéis.
--
-- Este arquivo existe separado de 001_schema.sql por um motivo só: criação de
-- banco e de papel é a única parte do DDL que nenhum dos dois engines escreve
-- da mesma forma. Isolando-a aqui, o schema em si fica portátil — e a última
-- coluna da tabela do ADR 0004 continua verdadeira.

CREATE DATABASE IF NOT EXISTS projecty;

-- Um papel por serviço, com permissão só no que lhe cabe (A2).
CREATE USER IF NOT EXISTS rental_core;
CREATE USER IF NOT EXISTS media_guard;
