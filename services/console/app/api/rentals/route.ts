import { createRental, failure, sameOrigin, session } from '../../../lib/server';
import { plates, dates } from '../../../lib/contracts';
export async function POST(request:Request) {
  try {
    sameOrigin(request);
    const data = await request.json();
    const selected = plates(data.plates);
    const date = dates(data.startDate,data.predictedEndDate);
    const auth = await session();
    const encoder = new TextEncoder();
    // Each batch is bounded to 100. The gateway remains the authoritative limiter.
    const stream = new ReadableStream({async start(controller) {
      await Promise.all(selected.map(async (plate,index) => {
        const result = await createRental(auth.token,auth.userId,plate,date,index);
        try {controller.enqueue(encoder.encode(JSON.stringify(result)+'\n'))} catch { /* client disconnected */ }
      }));
      try {controller.close()} catch { /* client disconnected */ }
    }});
    return new Response(stream,{headers:{'Content-Type':'application/x-ndjson','Cache-Control':'no-store'}});
  } catch(error) {return failure(error)}
}
