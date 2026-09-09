-- As tabelas de identidade: a metade aberta da Contradição 03.
--
-- O schema alvo tinha `motorcycles`, `rentals`, `outbox` e `inbox`, e nenhuma
-- linha sobre quem é o piloto -- enquanto `rentals.rider_id` já era uma
-- referência a um registro que serviço nenhum possuía. Este arquivo é onde
-- aquela contradição fecha em código, e não em documento.
--
-- Mesmo subconjunto portátil dos anteriores: o CI aplica este arquivo ao
-- CockroachDB e ao PostgreSQL, e o glob de scripts/verify-schema-portability.sh
-- o inclui por existir.

-- O identificador do usuário é o `sub` do token, é `rentals.rider_id` e é a
-- chave de partição de `rider_positions` no Cassandra. São o MESMO valor, e o
-- ADR 0012 decidiu isso depois do achado B10 -- `Rider.Id` e `Rider.UserId`
-- usados como se fossem uma chave só. Aqui não há surrogate para divergir.
CREATE TABLE IF NOT EXISTS users (
    id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    email            TEXT NOT NULL,
    -- O e-mail normalizado existe separado do original porque a unicidade é
    -- sobre a forma canônica e a exibição é sobre o que a pessoa digitou.
    -- Guardar só um dos dois obriga a escolher entre aceitar dois cadastros que
    -- diferem por maiúsculas e devolver o e-mail de volta descaracterizado.
    email_normalized TEXT NOT NULL,
    password_hash    TEXT NOT NULL,
    name             TEXT NOT NULL,
    user_type        TEXT NOT NULL CHECK (user_type IN ('admin', 'rider')),
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS one_user_per_email ON users (email_normalized);

CREATE TABLE IF NOT EXISTS user_roles (
    user_id UUID NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    role    TEXT NOT NULL,
    PRIMARY KEY (user_id, role)
);

-- O registro regulatório do piloto, separado da credencial.
--
-- Mesmo banco e mesmo processo -- o ADR 0023 explica por que o `rider-core`
-- separado do ADR 0012 não se justificou --, mas tabela separada de propósito:
-- o e-mail e o hash de senha são de um ciclo de vida, o CNPJ e a CNH são de
-- outro. Um SELECT de login não precisa tocar CNH nenhuma, e um GRANT futuro
-- pode separá-las sem migrar dado.
--
-- `user_id` é chave primária e estrangeira ao mesmo tempo: um piloto NÃO tem
-- identificador próprio. Foi um surrogate a mais que produziu o achado B10.
CREATE TABLE IF NOT EXISTS riders (
    user_id        UUID PRIMARY KEY REFERENCES users (id) ON DELETE CASCADE,
    cnpj           TEXT NOT NULL,
    date_of_birth  DATE NOT NULL,
    cnh_number     TEXT NOT NULL,
    cnh_type       TEXT NOT NULL CHECK (cnh_type IN ('A', 'B', 'AB')),
    -- O objeto da CNH pertence ao media-guard; aqui fica só o ponteiro para
    -- ele. É a divisão arquivo/registro do ADR 0012, que sobrevive inteira.
    cnh_object_key TEXT,
    verified       BOOL NOT NULL DEFAULT false,
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS one_rider_per_cnpj ON riders (cnpj);

-- O refresh token mora aqui, e não no Redis, pela pergunta que decide o ADR
-- 0017: perder o Redis desloga todo mundo? Com os tokens no Redis, sim -- e a
-- tabela de degradação passaria a ter "limitação de taxa degradada" e "toda
-- sessão da plataforma destruída" na mesma linha.
--
-- A coluna guarda o SHA-256 do token, nunca o token. Quem lê o banco não
-- consegue renovar sessão nenhuma; consegue, no máximo, invalidar.
CREATE TABLE IF NOT EXISTS refresh_tokens (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     UUID NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    token_hash  BYTEA NOT NULL,
    -- Uma família por login. Renovar consome o token e emite outro na mesma
    -- família; reapresentar um token já consumido é sinal de roubo, e a reação
    -- é revogar a família inteira em vez de só recusar a chamada.
    family_id   UUID NOT NULL,
    issued_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at  TIMESTAMPTZ NOT NULL,
    consumed_at TIMESTAMPTZ,
    revoked_at  TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS one_refresh_token_per_hash ON refresh_tokens (token_hash);
CREATE INDEX IF NOT EXISTS refresh_tokens_by_family ON refresh_tokens (family_id);
CREATE INDEX IF NOT EXISTS refresh_tokens_by_user ON refresh_tokens (user_id, expires_at DESC);

-- As chaves de assinatura, com a privada selada.
--
-- O ADR 0013 afirma que nenhum validador guarda segredo capaz de assinar. Se a
-- privada estivesse aqui em claro, quem lesse o banco passaria a ser um
-- emissor -- e o banco é lido por backup, por réplica e por quem depurar. A
-- semente Ed25519 é selada com AES-256-GCM sob IDENTITY_KEY_ENCRYPTION_KEY,
-- que vive no ambiente do processo e não no store.
CREATE TABLE IF NOT EXISTS signing_keys (
    kid          TEXT PRIMARY KEY,
    public_key   BYTEA NOT NULL,
    sealed_seed  BYTEA NOT NULL,
    seal_nonce   BYTEA NOT NULL,
    -- `active` assina; `retiring` só valida. A rotação promove uma chave nova e
    -- rebaixa a anterior, que continua publicada no JWKS até `retire_after` --
    -- é isso que faz um token já emitido sobreviver à rotação.
    status       TEXT NOT NULL CHECK (status IN ('active', 'retiring')),
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    retire_after TIMESTAMPTZ
);

-- Uma chave assinando por vez. Duas réplicas subindo ao mesmo tempo num banco
-- vazio disputam esta linha, e o índice decide -- sem lock de aplicação e sem
-- a janela em que as duas geram chaves diferentes e publicam JWKS distintos.
CREATE UNIQUE INDEX IF NOT EXISTS one_active_signing_key
    ON signing_keys (status)
    WHERE status = 'active';

GRANT USAGE ON SCHEMA public TO identity;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE users, user_roles, riders, refresh_tokens, signing_keys TO identity;
