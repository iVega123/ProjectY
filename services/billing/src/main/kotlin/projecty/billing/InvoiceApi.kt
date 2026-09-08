package projecty.billing

import com.fasterxml.jackson.databind.ObjectMapper
import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpHandler
import org.slf4j.LoggerFactory
import java.net.URI
import java.nio.charset.StandardCharsets
import java.util.UUID

/**
 * A leitura da nota, atrás do portão.
 *
 * Duas rotas, e a segunda existe por contrato e não por desempenho: o #138 diz
 * que compor uma tela não pode custar uma requisição por linha, e sem o lote o
 * N+1 não desaparece -- só se muda do navegador para o BFF.
 *
 *   GET /api/invoices/{rentalId}
 *   GET /api/invoices?rentalIds=a,b,c
 *
 * Não há rota de escrita. Uma nota é emitida pelo consumo de `rental.closed`, na
 * mesma transação do inbox; um POST aqui seria um segundo caminho para o mesmo
 * efeito, e o segundo caminho é onde a garantia de exatamente-uma-vez vaza.
 */
class InvoiceApi(
    private val reads: InvoiceReads,
    private val identity: GatewayIdentity,
) : HttpHandler {
    private val log = LoggerFactory.getLogger(InvoiceApi::class.java)
    private val json = ObjectMapper()

    override fun handle(exchange: HttpExchange) {
        exchange.use {
            try {
                respond(exchange)
            } catch (error: Exception) {
                // A mensagem da exceção pode carregar a consulta, o host ou o
                // papel do banco. O cliente recebe o código; o motivo fica no log.
                log.error("Invoice read failed", error)
                write(exchange, 500, mapOf("detail" to "The invoice could not be read."))
            }
        }
    }

    private fun respond(exchange: HttpExchange) {
        if (exchange.requestMethod != "GET") {
            write(exchange, 405, mapOf("detail" to "Invoices are read-only."))
            return
        }

        val caller =
            identity.verify(
                { exchange.requestHeaders[it] ?: emptyList() },
                "GET",
                pathAndQuery(exchange.requestURI),
            )
        if (caller == null) {
            write(exchange, 401, mapOf("detail" to "A valid gateway identity envelope is required."))
            return
        }

        val single = exchange.requestURI.rawPath.removePrefix(BASE).trim('/')
        if (single.isNotEmpty()) {
            val rentalId = runCatching { UUID.fromString(single) }.getOrNull()
            if (rentalId == null) {
                write(exchange, 400, mapOf("detail" to "'$single' is not a rental id."))
                return
            }
            val found = reads.byRentalIds(listOf(rentalId), caller.subject, caller.isAdmin)
            if (found.isEmpty()) {
                // 404 e não 403 mesmo quando a nota existe e é de outro: a
                // diferença entre as duas respostas é um oráculo de existência.
                write(exchange, 404, mapOf("detail" to "No invoice for rental $single."))
            } else {
                write(exchange, 200, found.first())
            }
            return
        }

        val requested =
            query(exchange.requestURI)["rentalIds"]
                ?.split(',')
                ?.map { it.trim() }
                ?.filter { it.isNotEmpty() }
                .orEmpty()
        if (requested.isEmpty()) {
            write(exchange, 400, mapOf("detail" to "At least one rental id is required."))
            return
        }
        if (requested.size > InvoiceReads.MAX_BATCH_SIZE) {
            write(
                exchange,
                400,
                mapOf("detail" to "A batch may request at most ${InvoiceReads.MAX_BATCH_SIZE} rental ids."),
            )
            return
        }
        val ids = requested.map { runCatching { UUID.fromString(it) }.getOrNull() }
        val unparseable = requested.zip(ids).firstOrNull { it.second == null }?.first
        if (unparseable != null) {
            write(exchange, 400, mapOf("detail" to "'$unparseable' is not a rental id."))
            return
        }

        write(exchange, 200, reads.byRentalIds(ids.filterNotNull(), caller.subject, caller.isAdmin))
    }

    private fun write(
        exchange: HttpExchange,
        status: Int,
        body: Any,
    ) {
        val bytes = json.writeValueAsBytes(body)
        exchange.responseHeaders.add("Content-Type", "application/json; charset=utf-8")
        exchange.sendResponseHeaders(status, bytes.size.toLong())
        exchange.responseBody.write(bytes)
    }

    companion object {
        const val BASE = "/api/invoices"

        /**
         * O caminho como o portão o assinou.
         *
         * Cru, e não decodificado: o portão assina a URI que envia, e uma
         * requisição que só bate depois de decodificada não é a mesma que ele
         * assinou. Ele já recusa caminho com `%` antes de assinar (o
         * `is_canonical_path`), então na prática os dois são iguais -- usar o cru
         * é o que mantém a igualdade verdadeira e não uma coincidência.
         */
        fun pathAndQuery(uri: URI): String = uri.rawPath + (uri.rawQuery?.let { "?$it" } ?: "")

        fun query(uri: URI): Map<String, String> =
            uri.rawQuery
                ?.split('&')
                ?.mapNotNull { pair ->
                    val separator = pair.indexOf('=')
                    if (separator <= 0) {
                        null
                    } else {
                        decode(pair.substring(0, separator)) to decode(pair.substring(separator + 1))
                    }
                }
                ?.toMap()
                .orEmpty()

        private fun decode(value: String): String = java.net.URLDecoder.decode(value, StandardCharsets.UTF_8)
    }
}
