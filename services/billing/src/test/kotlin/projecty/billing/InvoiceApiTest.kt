package projecty.billing

import com.fasterxml.jackson.databind.ObjectMapper
import com.sun.net.httpserver.HttpServer
import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.TestInstance
import java.net.InetSocketAddress
import java.net.URI
import java.net.http.HttpClient
import java.net.http.HttpRequest
import java.net.http.HttpResponse
import java.nio.charset.StandardCharsets
import java.time.Instant
import java.util.Base64
import java.util.UUID
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * A leitura da nota pela borda: quem pode ver o quê, e o que é recusado.
 *
 * O envelope é assinado aqui, e isso é deliberado -- o assunto desta classe é o
 * comportamento da rota, não a verificação. Que a verificação concorda com o
 * portão de verdade é o que o `GatewayIdentityTest` prova, contra um envelope
 * que saiu dele.
 */
@TestInstance(TestInstance.Lifecycle.PER_CLASS)
class InvoiceApiTest {
    private val key = "k".repeat(32).toByteArray(StandardCharsets.UTF_8)
    private val audience = "projecty.billing"
    private val json = ObjectMapper()
    private val http = HttpClient.newHttpClient()
    private lateinit var server: HttpServer
    private lateinit var base: String

    @BeforeAll
    fun start() {
        val identity = GatewayIdentity(key, "local-v1", audience)
        server =
            HttpServer.create(InetSocketAddress(0), 0).apply {
                createContext(InvoiceApi.BASE, InvoiceApi(InvoiceReads(TestDatabase.dataSource), identity))
                start()
            }
        base = "http://127.0.0.1:${server.address.port}"
    }

    @AfterAll
    fun stop() = server.stop(0)

    @Test
    fun `o dono le a propria nota`() {
        val invoice = seed("rider-api-a")

        val response = get("${InvoiceApi.BASE}/${invoice.rentalId}", "rider-api-a")

        assertEquals(200, response.statusCode())
        val body = json.readTree(response.body())
        assertEquals(invoice.rentalId, body["rentalId"].asText())
        assertEquals(13_800L, body["totalMinor"].asLong())
        assertEquals("BRL", body["currency"].asText())
    }

    /**
     * 404 e não 403, mesmo existindo.
     *
     * A diferença entre as duas respostas diz "esta nota existe, mas não é sua",
     * que é exatamente o que alguém varrendo ids quer ouvir. A resposta precisa
     * ser indistinguível da de um id inexistente.
     */
    @Test
    fun `a nota de outro piloto e indistinguivel de uma que nao existe`() {
        val theirs = seed("rider-api-b")

        val existing = get("${InvoiceApi.BASE}/${theirs.rentalId}", "rider-api-c")
        val absent = get("${InvoiceApi.BASE}/${UUID.randomUUID()}", "rider-api-c")

        assertEquals(404, existing.statusCode())
        assertEquals(404, absent.statusCode())
        // O corpo só difere no id que o próprio chamador enviou: nada na
        // resposta diz se a nota existe.
        assertEquals(
            existing.body().replace(theirs.rentalId, "ID"),
            absent.body().replace(Regex("[0-9a-f-]{36}"), "ID"),
        )
    }

    @Test
    fun `um administrador le a nota de qualquer um`() {
        val theirs = seed("rider-api-d")

        val response = get("${InvoiceApi.BASE}/${theirs.rentalId}", "an-admin", roles = "Admin")

        assertEquals(200, response.statusCode())
        assertEquals(theirs.rentalId, json.readTree(response.body())["rentalId"].asText())
    }

    @Test
    fun `o lote devolve as do chamador e omite as alheias`() {
        val mine = seed("rider-api-e")
        val alsoMine = seed("rider-api-e")
        val theirs = seed("rider-api-f")

        val response =
            get(
                "${InvoiceApi.BASE}?rentalIds=${mine.rentalId},${alsoMine.rentalId},${theirs.rentalId}",
                "rider-api-e",
            )

        assertEquals(200, response.statusCode())
        val returned = json.readTree(response.body()).map { it["rentalId"].asText() }.toSet()
        assertEquals(setOf(mine.rentalId, alsoMine.rentalId), returned)
    }

    @Test
    fun `um lote acima do teto e recusado em vez de servido`() {
        val ids = (0 until InvoiceReads.MAX_BATCH_SIZE + 1).joinToString(",") { UUID.randomUUID().toString() }

        val response = get("${InvoiceApi.BASE}?rentalIds=$ids", "rider-api-g")

        // Um lote sem teto é uma consulta arbitrária escrita pelo cliente.
        assertEquals(400, response.statusCode())
    }

    @Test
    fun `um lote vazio ou com id ilegivel e recusado`() {
        assertEquals(400, get("${InvoiceApi.BASE}?rentalIds=", "rider-api-h").statusCode())
        assertEquals(400, get(InvoiceApi.BASE, "rider-api-h").statusCode())
        assertEquals(400, get("${InvoiceApi.BASE}?rentalIds=not-a-guid", "rider-api-h").statusCode())
        assertEquals(400, get("${InvoiceApi.BASE}/not-a-guid", "rider-api-h").statusCode())
    }

    @Test
    fun `sem o envelope do portao nao se le nada`() {
        val mine = seed("rider-api-i")

        val response =
            http.send(
                HttpRequest.newBuilder(URI.create(base + InvoiceApi.BASE + "/" + mine.rentalId)).GET().build(),
                HttpResponse.BodyHandlers.ofString(),
            )

        assertEquals(401, response.statusCode())
        assertTrue(!response.body().contains(mine.rentalId))
    }

    /**
     * O envelope é assinado para um caminho. Reapresentá-lo noutro é o ataque
     * que a assinatura existe para impedir, e a rota precisa recusá-lo mesmo com
     * a chave certa.
     */
    @Test
    fun `um envelope assinado para outro caminho nao serve`() {
        val mine = seed("rider-api-j")
        val signedFor = "${InvoiceApi.BASE}/${UUID.randomUUID()}"

        val response =
            http.send(
                envelope(
                    HttpRequest.newBuilder(URI.create("$base${InvoiceApi.BASE}/${mine.rentalId}")).GET(),
                    "rider-api-j",
                    "Rider",
                    signedFor,
                ).build(),
                HttpResponse.BodyHandlers.ofString(),
            )

        assertEquals(401, response.statusCode())
    }

    @Test
    fun `escrever numa nota nao e uma rota`() {
        val response =
            http.send(
                envelope(
                    HttpRequest.newBuilder(URI.create(base + InvoiceApi.BASE))
                        .POST(HttpRequest.BodyPublishers.ofString("{}")),
                    "rider-api-k",
                    "Rider",
                    InvoiceApi.BASE,
                ).build(),
                HttpResponse.BodyHandlers.ofString(),
            )

        assertEquals(405, response.statusCode())
    }

    // ------------------------------------------------------------------ apoio

    /**
     * O lote responde com tudo o que o console declarou precisar.
     *
     * Os nomes dos campos vêm de `contracts/reads.json`, e não desta classe:
     * renomear `totalMinor` aqui deixa este teste vermelho sem que ninguém
     * precise lembrar de atualizá-lo, que é a diferença entre um contrato e um
     * comentário.
     */
    @Test
    fun `o lote responde o que o console declarou`() {
        val declared = DeclaredRead.of("billing", InvoiceApi.BASE)
        assertEquals("GET", declared.method)
        assertEquals(InvoiceReads.MAX_BATCH_SIZE, declared.maxBatch)

        val first = seed("rider-contract")
        val second = seed("rider-contract")

        val response =
            get(
                "${InvoiceApi.BASE}?${declared.idsParameter}=${first.rentalId},${second.rentalId}",
                "rider-contract",
            )

        assertEquals(200, response.statusCode())
        val invoices = json.readTree(response.body())
        assertEquals(2, invoices.size())
        for (invoice in invoices) {
            for (field in declared.fields) {
                assertTrue(
                    invoice.has(field),
                    "contracts/reads.json declares '$field', and the batch did not answer with it.",
                )
            }
        }
    }

    private fun get(
        pathAndQuery: String,
        subject: String,
        roles: String = "Rider",
    ): HttpResponse<String> =
        http.send(
            envelope(
                HttpRequest.newBuilder(URI.create(base + pathAndQuery)).GET(),
                subject,
                roles,
                pathAndQuery,
            ).build(),
            HttpResponse.BodyHandlers.ofString(),
        )

    private fun envelope(
        builder: HttpRequest.Builder,
        subject: String,
        roles: String,
        pathAndQuery: String,
        method: String = "GET",
    ): HttpRequest.Builder {
        val issuedAt = Instant.now().epochSecond.toString()
        val canonical =
            listOf("v1", "local-v1", subject, roles, issuedAt, method, pathAndQuery, audience)
                .joinToString("\n")
        val signature =
            Mac.getInstance("HmacSHA256").run {
                init(SecretKeySpec(key, "HmacSHA256"))
                Base64.getUrlEncoder().withoutPadding()
                    .encodeToString(doFinal(canonical.toByteArray(StandardCharsets.UTF_8)))
            }
        return builder
            .header(GatewayIdentity.KEY_ID_HEADER, "local-v1")
            .header(GatewayIdentity.SUBJECT_HEADER, subject)
            .header(GatewayIdentity.ROLES_HEADER, roles)
            .header(GatewayIdentity.ISSUED_AT_HEADER, issuedAt)
            .header(GatewayIdentity.SIGNATURE_HEADER, "v1=$signature")
    }

    private fun seed(riderId: String): ClosedRental {
        val rentalId = UUID.randomUUID().toString()
        val start = 1_767_225_600_000L
        val rental =
            ClosedRental(
                eventId = "$rentalId:rental.closed:v1",
                rentalId = rentalId,
                riderId = riderId,
                currency = "BRL",
                agreedTotalMinor = 21_000L,
                planDays = 7,
                startedAtMs = start,
                predictedEndAtMs = start + 7 * 86_400_000L,
                endedAtMs = start + 4 * 86_400_000L,
                riderName = "Ada Lovelace",
                traceParent = null,
            )
        val settled =
            Settlement.settle(
                rental.agreedTotalMinor,
                rental.planDays,
                rental.startedAtMs,
                rental.predictedEndAtMs,
                rental.endedAtMs,
            )
        val issued = Invoices(TestDatabase.dataSource).issue(rental.eventId, rental, settled)
        assertEquals(Outcome.ISSUED, issued.outcome, "a fixture precisa realmente emitir a nota")
        return rental
    }
}
