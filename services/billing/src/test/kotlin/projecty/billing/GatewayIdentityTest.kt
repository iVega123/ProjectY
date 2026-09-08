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
 * O vetor abaixo não foi escrito à mão: saiu do próprio portão, pelo caminho de
 * assinatura dele, no teste `signs_invoice_reads_for_the_billing_audience`
 * (services/api-gateway/src/lib.rs). Isso é o que o torna útil -- um vetor que
 * eu mesmo assinasse provaria apenas que esta classe concorda consigo mesma, e
 * o risco de uma segunda implementação não é esse. É a string canônica divergir.
 *
 * As duas pontas ficam presas: o teste em Rust fixa o que o portão assina para
 * uma rota de fatura, e este fixa o que esta classe aceita. Uma mudança de um
 * dos lados falha de algum dos dois em vez de virar uma porta aberta.
 */
class GatewayIdentityTest {
    private val key = "x".repeat(32).toByteArray(StandardCharsets.UTF_8)
    private val issuedAt = 1_788_905_813L
    private val signature = "v1=LyyXb6bQDnqqgS4Ue9Am2Ccq6Uhv4-OWN70fb_Olim8"
    private val path = "/api/invoices?rentalIds=a,b"

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
        GatewayIdentity.SIGNATURE_HEADER to listOf(signed),
    )

    private fun GatewayIdentity.check(
        headers: Map<String, List<String>>,
        method: String = "GET",
        pathAndQuery: String = path,
    ) = verify({ headers[it] ?: emptyList() }, method, pathAndQuery)

    @Test
    fun `o envelope que o portao assinou e aceito`() {
        val caller = identity().check(envelope())

        assertNotNull(caller)
        assertEquals("rider-123", caller.subject)
        assertEquals(listOf("Rider"), caller.roles)
        assertTrue(!caller.isAdmin)
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
        for (bad in listOf("-1788905813", "+1788905813", " 1788905813", "1788905813 ", "0x1")) {
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
