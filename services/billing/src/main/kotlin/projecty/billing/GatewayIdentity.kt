package projecty.billing

import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import java.time.Clock
import java.time.Duration
import java.util.Base64
import java.util.HexFormat
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * O envelope de identidade do ADR 0008, verificado aqui.
 *
 * O portão é a única fronteira que valida um token; para trás dele ele assina um
 * envelope HMAC por requisição, e cada serviço confere. Isto é a segunda
 * implementação da conferência -- a primeira é `Shared/Security/
 * GatewayIdentityAuthentication.cs` -- e uma segunda implementação de um limite
 * de segurança é uma coisa que se assume de olhos abertos: qualquer divergência
 * entre as duas é uma porta.
 *
 * Por isso ela é literal. A string canônica é copiada campo a campo, na mesma
 * ordem, com o mesmo separador; o conjunto de caracteres aceitos é o mesmo; a
 * janela de tempo é a mesma; a comparação é de tempo constante. O teste
 * `GatewayIdentityTest` carrega vetores gerados pelo assinante do portão, e é
 * ele que faz uma divergência aparecer como falha em vez de como acesso.
 *
 * Não reimplementa a assinatura -- só a verificação. O billing não fala com
 * ninguém para trás dele, e uma chave que só verifica é uma chave que não
 * emite.
 */
class GatewayIdentity(
    private val signingKey: ByteArray,
    private val signingKeyId: String,
    private val audience: String,
    private val maximumAge: Duration = Duration.ofSeconds(30),
    private val clockSkew: Duration = Duration.ofSeconds(5),
    private val clock: Clock = Clock.systemUTC(),
) {
    init {
        require(signingKey.size >= 32) { "The gateway identity signing key must contain at least 32 bytes." }
        require(audience.isNotBlank()) { "The gateway identity audience is required." }
    }

    /**
     * Quem está chamando, segundo o portão.
     *
     * `Admin` compara sem diferenciar maiúsculas porque é o que o
     * `ClaimsPrincipal.IsInRole` do lado .NET faz -- e as duas camadas
     * discordarem sobre quem é administrador seria pior do que qualquer das duas
     * regras isolada.
     */
    data class Caller(
        val subject: String,
        val roles: List<String>,
    ) {
        val isAdmin: Boolean get() = roles.any { it.equals(ADMIN_ROLE, ignoreCase = true) }
    }

    /**
     * O chamador, ou null quando o envelope não vale. Nunca uma exceção com a
     * razão: quem não passou não precisa saber por qual dos sete motivos.
     *
     * `body` são os bytes do corpo como chegaram. A assinatura `v2` os cobre, e
     * é o que impede um envelope capturado de servir, na mesma rota e dentro da
     * janela, para outro corpo (#191).
     *
     * A `v1`, que não cobria o corpo, deixou de valer no #274: aceitá-la na falta
     * da `v2` deixava quem remove o cabeçalho `v2` de um envelope capturado voltar
     * à assinatura que não cobre o corpo. Um `x-identity-signature` que ainda
     * chegue, de um portão anterior, é ignorado.
     */
    fun verify(
        header: (String) -> List<String>,
        method: String,
        pathAndQuery: String,
        body: ByteArray,
    ): Caller? {
        val values = HEADERS.map { header(it) }
        if (values.any { it.size != 1 }) return null
        val (keyId, subject, roles, issuedAtValue) = values.map { it[0] }

        // A assinatura também exatamente uma vez.
        val signature = header(SIGNATURE_V2_HEADER).singleOrNull() ?: return null

        if (keyId != signingKeyId) return null
        if (!isSafeComponent(keyId, 128)) return null
        if (!isSafeComponent(subject, 512)) return null
        // NumberStyles.None do lado .NET: só dígitos. `toLongOrNull` aceitaria um
        // sinal, e um carimbo negativo passaria pela janela de tempo por baixo.
        if (!DIGITS.matches(issuedAtValue)) return null
        val issuedAt = issuedAtValue.toLongOrNull() ?: return null

        val now = clock.instant().epochSecond
        if (issuedAt > now + clockSkew.seconds) return null
        if (issuedAt < now - maximumAge.seconds) return null

        val parsedRoles = parseRoles(roles) ?: return null

        val bound =
            listOf(
                keyId,
                subject,
                roles,
                issuedAtValue,
                method,
                pathAndQuery,
                audience,
            ).joinToString("\n")
        if (!verifySignature("v2\n$bound\n${bodyDigest(body)}", signature)) return null

        return Caller(subject, parsedRoles)
    }

    private fun verifySignature(
        canonical: String,
        signature: String,
    ): Boolean {
        if (!signature.startsWith(SIGNATURE_PREFIX)) return false
        val supplied =
            try {
                // O portão remove o preenchimento; o decodificador de URL do Java
                // aceita a entrada sem ele e recusa comprimentos impossíveis.
                Base64.getUrlDecoder().decode(signature.substring(SIGNATURE_PREFIX.length))
            } catch (_: IllegalArgumentException) {
                return false
            }
        val expected =
            Mac.getInstance("HmacSHA256").run {
                init(SecretKeySpec(signingKey, "HmacSHA256"))
                doFinal(canonical.toByteArray(StandardCharsets.UTF_8))
            }
        // MessageDigest.isEqual é de tempo constante desde o Java 7, e é o que
        // impede alguém de descobrir a assinatura um byte por vez.
        return MessageDigest.isEqual(expected, supplied)
    }

    private fun parseRoles(value: String): List<String>? {
        if (value.isEmpty()) return emptyList()
        if (value.length > 1024) return null
        val roles = value.split(",")
        return if (roles.size <= 32 && roles.all { isSafeComponent(it, 512) }) roles else null
    }

    companion object {
        const val KEY_ID_HEADER = "x-identity-key-id"
        const val SUBJECT_HEADER = "x-identity-subject"
        const val ROLES_HEADER = "x-identity-roles"
        const val ISSUED_AT_HEADER = "x-identity-issued-at"
        const val SIGNATURE_V2_HEADER = "x-identity-signature-v2"
        private const val SIGNATURE_PREFIX = "v2="
        const val ADMIN_ROLE = "Admin"

        /**
         * O maior corpo que o portão assina. Um corpo maior não saiu dele, e ler
         * além disso seria memória de graça para quem alcança a porta.
         */
        const val MAX_SIGNED_BODY_BYTES = 32 * 1024 * 1024

        /**
         * Os cabeçalhos que todo envelope traz exatamente uma vez, na ordem da
         * string canônica -- que é a que o `destructuring` acima assume. A
         * assinatura fica de fora porque não entra na string que ela assina.
         */
        val HEADERS =
            listOf(KEY_ID_HEADER, SUBJECT_HEADER, ROLES_HEADER, ISSUED_AT_HEADER)

        private val DIGITS = Regex("^[0-9]+$")

        /**
         * A última linha da string canônica: SHA-256 do corpo, em hex minúsculo. Sem corpo e
         * corpo vazio são o mesmo digest, o de zero bytes.
         */
        fun bodyDigest(body: ByteArray): String =
            HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(body))

        /**
         * ASCII imprimível menos o espaço e menos a vírgula.
         *
         * A vírgula fica de fora porque é o separador dos papéis: aceita-la
         * deixaria um papel chamado "a,Admin" virar dois na leitura de quem
         * separa, que é exatamente uma escalada de privilégio.
         */
        fun isSafeComponent(
            value: String,
            maximumLength: Int,
        ): Boolean =
            value.isNotEmpty() &&
                value.length <= maximumLength &&
                value.all { it in '!'..'+' || it in '-'..'~' }
    }
}
