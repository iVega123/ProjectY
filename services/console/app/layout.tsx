import type { Metadata } from 'next';
import './style.css';
export const metadata:Metadata = {title:'ProjectY / Operations',description:'Live rental operations, distributed traces and controlled load.'};
export default function Layout({children}:{children:React.ReactNode}) {return <html lang="en"><body>{children}</body></html>}
