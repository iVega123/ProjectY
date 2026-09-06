import { cookies } from 'next/headers';
import { failure, origin, sameOrigin, session, upstream } from '../../../lib/server';
export async function POST(request:Request) {
  try {
    sameOrigin(request);
    const {token} = await request.json();
    if (typeof token !== 'string' || token.length > 8192 || !/^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(token)) throw new Error('Invalid access token');
    const response = await upstream('/api/Rental/user?pageSize=1',token);
    if (!response.ok) return Response.json({error:'Sign-in failed',status:response.status},{status:response.status});
    (await cookies()).set('projecty-session',token,{httpOnly:true,sameSite:'strict',secure:origin.startsWith('https:'),path:'/',maxAge:3600});
    return Response.json({signedIn:true});
  } catch(error) {return failure(error)}
}
export async function GET(request:Request) {try {const data = await session(new URL(request.url).searchParams.get('cursor') ?? ''); return Response.json({userId:data.userId,rentals:data.rentals})} catch(error) {return failure(error)}}
export async function DELETE(request:Request) {try {sameOrigin(request); (await cookies()).delete('projecty-session'); return Response.json({signedIn:false})} catch(error) {return failure(error)}}
