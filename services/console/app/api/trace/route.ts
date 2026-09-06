import { failure, session, verifyGrant } from '../../../lib/server';
import { traceSpans } from '../../../lib/contracts';
export async function GET(request:Request) {
  try {
    const auth = await session();
    const params = new URL(request.url).searchParams;
    const id = params.get('id') ?? '';
    if (!/^[a-f0-9]{32}$/.test(id) || !verifyGrant(params.get('grant') ?? '',id,auth.userId)) return Response.json({error:'Trace access denied'},{status:403});
    const response = await fetch((process.env.TEMPO_URL ?? 'http://tempo:3200')+'/api/traces/'+id,
      {headers:{Accept:'application/json'},cache:'no-store',signal:AbortSignal.timeout(5000)});
    if (response.status === 404) return Response.json({traceId:id,spans:[],pending:true});
    if (!response.ok) throw new Error('Trace storage unavailable');
    return Response.json({traceId:id,spans:traceSpans(await response.json()),pending:false});
  } catch(error) {return failure(error)}
}
