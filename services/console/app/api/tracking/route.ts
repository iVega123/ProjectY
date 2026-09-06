import { failure, session, ticket } from '../../../lib/server';
export async function GET(request:Request) {
  try {
    const auth = await session();
    const id = new URL(request.url).searchParams.get('rentalId');
    const rental = auth.rentals.items.find(r => r.rentalId === id && r.userId === auth.userId && !r.actualEndDate);
    if (!rental) return Response.json({error:'Active rental not found in your current page'},{status:403});
    return Response.json({ticket:ticket(rental.rentalId,auth.userId),socketUrl:process.env.TELEMETRY_PUBLIC_URL ?? 'ws://localhost:4000/socket/websocket'});
  } catch(error) {return failure(error)}
}
