'use client';
import { useEffect, useRef } from 'react';
import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import type { Position } from '../lib/types';
export default function LiveMap({position}:{position:Position|null}) {
  const div = useRef<HTMLDivElement>(null);
  const map = useRef<L.Map|null>(null);
  const marker = useRef<L.CircleMarker|null>(null);
  useEffect(() => {
    if (!div.current) return;
    map.current = L.map(div.current,{zoomControl:false}).setView([-3.119,-60.021],12);
    L.control.zoom({position:'bottomright'}).addTo(map.current);
    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png',{attribution:'© OpenStreetMap contributors',maxZoom:19}).addTo(map.current);
    return () => {map.current?.remove(); map.current=null; marker.current=null};
  },[]);
  useEffect(() => {
    if (!position || !map.current) return;
    const latlng:L.LatLngExpression = [position.latitude,position.longitude];
    if (!marker.current) marker.current = L.circleMarker(latlng,{radius:10,color:'#b9ff70',fillColor:'#183b24',fillOpacity:1,weight:4}).addTo(map.current);
    else marker.current.setLatLng(latlng);
    map.current.panTo(latlng);
  },[position]);
  return <div className="map-canvas" ref={div} aria-label="Live rental position map" />;
}
