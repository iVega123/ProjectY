-- O access token que cada refresh token acompanha (#59).
--
-- O portão verifica o access token sozinho e só pergunta ao Redis antes de
-- criar um aluguel, em `projecty:revoked:jti:{jti}` (ADR 0017). Para que sair
-- negue ali o token que ainda está na mão de alguém, o identity precisa saber
-- quais `jti` a sessão emitiu -- e a sessão é a família destas linhas. Cada
-- linha guarda o `jti` emitido junto com ela e quando ele expira.
--
-- As colunas são anuláveis: uma linha anterior a esta migração não tem o que
-- negar, e o token dela expira em no máximo cinco minutos.
ALTER TABLE refresh_tokens ADD COLUMN IF NOT EXISTS access_token_id UUID;
ALTER TABLE refresh_tokens ADD COLUMN IF NOT EXISTS access_expires_at TIMESTAMPTZ;

-- O que a ressincronização lê a cada poucos segundos: tokens revogados que
-- ainda não expiraram. Parcial, porque quase nenhuma linha é revogada, e é esse
-- conjunto pequeno que precisa voltar ao Redis quando ele volta vazio.
CREATE INDEX IF NOT EXISTS revoked_access_tokens_by_expiry
    ON refresh_tokens (access_expires_at)
    WHERE revoked_at IS NOT NULL;
