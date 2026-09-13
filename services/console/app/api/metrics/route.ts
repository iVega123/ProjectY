import { failure, identity, metricsTicket } from '../../../lib/server';

// Os três números do painel são globais e chegam por push do telemetry, no
// tópico metrics:global (#194). Esta rota só autentica a aba, uma vez por
// conexão: o portão confere a sessão sem ler aluguel nenhum, e a resposta é o
// ticket do socket.
export async function GET() {
  try {
    const auth = await identity();
    return Response.json({ticket:metricsTicket(auth.userId),
      socketUrl:process.env.TELEMETRY_PUBLIC_URL ?? 'ws://localhost:4000/socket/websocket'});
  } catch(error) {return failure(error)}
}
