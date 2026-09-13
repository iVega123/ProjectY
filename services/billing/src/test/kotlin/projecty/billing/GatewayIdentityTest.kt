package projecty.billing

import java.nio.charset.StandardCharsets
import java.time.Clock
import java.time.Duration
import java.time.Instant
import java.time.ZoneOffset
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * A verificação do envelope, contra um envelope de verdade.
 *
 * O vetor abaixo é o que `signs_the_v2_envelopes_the_verifiers_pin`, em
 * services/api-gateway/src/auth.rs, prova que o portão produz para uma rota de
 * fatura. Os valores foram calculados à parte, com openssl, a partir da string
 * canônica do ADR 0008. Isso é o que o torna útil -- um vetor que eu mesmo
 * assinasse provaria apenas que esta classe concorda consigo mesma, e o risco de
 * uma segunda implementação não é esse. É a string canônica divergir.
 *
 * As duas pontas ficam presas: o teste em Rust fixa o que o portão assina, e
 * este fixa o que esta classe aceita. Uma mudança de um dos lados falha de algum
 * dos dois em vez de virar uma porta aberta. É um GET, e por isso é também o
 * vetor da regra do corpo vazio.
 */
class GatewayIdentityTest {
    private val key = "x".repeat(32).toByteArray(StandardCharsets.UTF_8)
    private val path = "/api/invoices?rentalIds=a,b"
    private val issuedAt = 1_789_300_000L
    private val signature = "v2=w5r-_x9wbmOcOjdb-icslt_9rsL7Llm5Uy3FVQwVRzE"

    /**
     * O `v1` que o portão anterior ao #274 mandava junto com este envelope, no
     * cabeçalho abaixo. Não cobre corpo nenhum.
     */
    private val previousV1 = "v1=jPNeT5mDVdMdyKNT5UXQYPfhI8Z-Hh9geXNfdYWKzl4"
    private val legacySignatureHeader = "x-identity-signature"

    /**
     * Um envelope só com `v1`, que um portão anterior ao #274 assinou para a
     * mesma rota, no `signs_invoice_reads_for_the_billing_audience` de então.
     */
    private val v1IssuedAt = 1_788_905_813L
    private val v1Signature = "v1=LyyXb6bQDnqqgS4Ue9Am2Ccq6Uhv4-OWN70fb_Olim8"

    private fun identity(
        audience: String = "projecty.billing",
        keyId: String = "local-v1",
        atSecond: Long = issuedAt,
    ) = GatewayIdentity(
        signingKey = key,
        signingKeyId = keyId,
        audience = audience,
        maximumAge = Duration.ofSeconds(30),
        clockSkew = Duration.ofSeconds(5),
        clock = Clock.fixed(Instant.ofEpochSecond(atSecond), ZoneOffset.UTC),
    )

    private fun envelope(
        keyId: String = "local-v1",
        subject: String = "rider-123",
        roles: String = "Rider",
        issued: String = issuedAt.toString(),
        signed: String = signature,
    ) = mapOf(
        GatewayIdentity.KEY_ID_HEADER to listOf(keyId),
        GatewayIdentity.SUBJECT_HEADER to listOf(subject),
        GatewayIdentity.ROLES_HEADER to listOf(roles),
        GatewayIdentity.ISSUED_AT_HEADER to listOf(issued),
        GatewayIdentity.SIGNATURE_V2_HEADER to listOf(signed),
    )

    private fun GatewayIdentity.check(
        headers: Map<String, List<String>>,
        method: String = "GET",
        pathAndQuery: String = path,
        body: ByteArray = ByteArray(0),
    ) = verify({ headers[it] ?: emptyList() }, method, pathAndQuery, body)

    @Test
    fun `o envelope que o portao assinou e aceito`() {
        val caller = identity().check(envelope())

        assertNotNull(caller)
        assertEquals("rider-123", caller.subject)
        assertEquals(listOf("Rider"), caller.roles)
        assertTrue(!caller.isAdmin)
    }

    /** O #191: o mesmo envelope, na mesma rota, dentro da janela, com outro corpo. */
    @Test
    fun `um envelope capturado nao serve para outro corpo`() {
        for (body in listOf("{}", " ", "rentalIds=a,c")) {
            assertNull(identity().check(envelope(), body = body.toByteArray()), body)
        }
    }

    /**
     * Um envelope que um portão anterior assinou, e que este verificador aceitava
     * até o #274. O relógio fica no instante da assinatura, para que a recusa seja
     * pela versão e não pela janela.
     */
    @Test
    fun `um envelope so com v1 e recusado`() {
        val v1Only =
            (envelope(issued = v1IssuedAt.toString()) - GatewayIdentity.SIGNATURE_V2_HEADER) +
                (legacySignatureHeader to listOf(v1Signature))

        assertNull(identity(atSecond = v1IssuedAt).check(v1Only))
    }

    /**
     * O rebaixamento que o #274 fecha: o envelope que o portão anterior mandava,
     * com as duas assinaturas, sem o cabeçalho `v2` e com outro corpo. O `v1` que
     * sobra vale para esse corpo, porque não cobre corpo nenhum.
     */
    @Test
    fun `tirar o v2 de um envelope capturado nao volta ao v1`() {
        val stripped =
            (envelope() - GatewayIdentity.SIGNATURE_V2_HEADER) +
                (legacySignatureHeader to listOf(previousV1))

        for (body in listOf("{}", "")) {
            assertNull(identity().check(stripped, body = body.toByteArray()), "corpo: '$body'")
        }
    }

    /**
     * Durante o rollout do #274, o portão anterior ainda manda as duas assinaturas
     * a este verificador. Isso passa pela `v2`, e o `v1` que veio junto não salva
     * um corpo trocado.
     */
    @Test
    fun `o v1 que um portao anterior ainda mande e ignorado`() {
        val withPreviousV1 = envelope() + (legacySignatureHeader to listOf(previousV1))

        assertNotNull(identity().check(withPreviousV1))
        assertNull(identity().check(withPreviousV1, body = "{}".toByteArray()))
    }

    @Test
    fun `sem corpo e corpo vazio sao o mesmo digest, o de zero bytes`() {
        assertEquals(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            GatewayIdentity.bodyDigest(ByteArray(0)),
        )
    }

    @Test
    fun `a assinatura vem exatamente uma vez`() {
        val v2 = GatewayIdentity.SIGNATURE_V2_HEADER

        assertNull(identity().check(envelope() - v2), "nenhuma")
        assertNull(identity().check(envelope() + (v2 to listOf(signature, signature))), "repetida")
        assertNull(identity().check(envelope(signed = "v1=" + signature.substring(3))), "versão errada")
        assertNull(identity().check(envelope(signed = previousV1)), "o v1 no lugar da v2")
    }

    @Test
    fun `um byte trocado na assinatura recusa`() {
        val tampered = signature.dropLast(1) + if (signature.last() == 'A') 'B' else 'A'

        assertNull(identity().check(envelope(signed = tampered)))
    }

    /**
     * A parte que um envelope roubado tentaria: a assinatura vale, mas para
     * outra requisição. Trocar o caminho, o método ou o serviço de destino
     * precisa invalidá-la -- senão um envelope emitido para ler uma fatura serve
     * para qualquer coisa que o portão roteie.
     */
    @Test
    fun `a assinatura nao vale para outro caminho, metodo ou destinatario`() {
        assertNull(identity().check(envelope(), pathAndQuery = "/api/invoices?rentalIds=a,c"))
        assertNull(identity().check(envelope(), pathAndQuery = "/api/invoices"))
        assertNull(identity().check(envelope(), method = "POST"))
        assertNull(identity(audience = "projecty.rental-operations").check(envelope()))
    }

    @Test
    fun `o assunto e os papeis fazem parte do que foi assinado`() {
        assertNull(identity().check(envelope(subject = "rider-124")))
        assertNull(identity().check(envelope(roles = "Admin")))
    }

    @Test
    fun `um envelope velho ou vindo do futuro recusa`() {
        // A janela é 30s para trás e 5s de folga para frente, como no lado .NET.
        assertNotNull(identity(atSecond = issuedAt + 30).check(envelope()))
        assertNull(identity(atSecond = issuedAt + 31).check(envelope()))
        assertNotNull(identity(atSecond = issuedAt - 5).check(envelope()))
        assertNull(identity(atSecond = issuedAt - 6).check(envelope()))
    }

    @Test
    fun `um key id diferente recusa antes de qualquer conta`() {
        assertNull(identity(keyId = "local-v2").check(envelope()))
    }

    @Test
    fun `um envelope incompleto ou repetido recusa`() {
        for (header in GatewayIdentity.HEADERS) {
            assertNull(identity().check(envelope() - header), "faltando $header")
        }
        // Dois valores para o mesmo header é a forma clássica de fazer duas
        // camadas lerem coisas diferentes: uma pega o primeiro, a outra o último.
        val duplicated =
            envelope() + (GatewayIdentity.SUBJECT_HEADER to listOf("rider-123", "attacker"))
        assertNull(identity().check(duplicated))
    }

    /**
     * Um carimbo negativo passaria pela borda de baixo da janela se fosse lido
     * como número com sinal. O lado .NET usa NumberStyles.None; aqui a regex faz
     * o mesmo.
     */
    @Test
    fun `um carimbo que nao e so digitos recusa`() {
        for (bad in listOf("-1789300000", "+1789300000", " 1789300000", "1789300000 ", "0x1")) {
            assertNull(identity().check(envelope(issued = bad)), bad)
        }
    }

    @Test
    fun `a virgula nao pode aparecer dentro de um papel`() {
        // Se a vírgula fosse aceita como caractere de papel, "a,Admin" chegaria
        // como um papel só do lado de quem assina e como dois do lado de quem
        // separa -- e o segundo seria Admin.
        assertTrue(!GatewayIdentity.isSafeComponent("a,Admin", 512))
        assertTrue(!GatewayIdentity.isSafeComponent("with space", 512))
        assertTrue(!GatewayIdentity.isSafeComponent("", 512))
        assertTrue(GatewayIdentity.isSafeComponent("Admin", 512))
    }

    @Test
    fun `Admin e reconhecido sem diferenciar maiusculas, como no lado NET`() {
        assertTrue(GatewayIdentity.Caller("s", listOf("admin")).isAdmin)
        assertTrue(GatewayIdentity.Caller("s", listOf("Rider", "ADMIN")).isAdmin)
        assertTrue(!GatewayIdentity.Caller("s", listOf("Rider")).isAdmin)
        assertTrue(!GatewayIdentity.Caller("s", listOf("Administrator")).isAdmin)
    }

    @Test
    fun `uma chave curta demais nao constroi o verificador`() {
        val error =
            runCatching {
                GatewayIdentity("short".toByteArray(), "local-v1", "projecty.billing")
            }.exceptionOrNull()

        assertTrue(error is IllegalArgumentException)
    }
}
