import type { Span } from './types.ts';
export function plates(input: unknown): string[] {
  if (!Array.isArray(input) || input.length < 1 || input.length > 100 ||
      input.some(p => typeof p !== 'string' || !/^[A-Z]{3}[0-9][A-Z0-9][0-9]{2}$/.test(p)))
    throw new Error('Provide 1–100 valid Brazilian licence plates.');
  return input;
}
export function dates(start: unknown, end: unknown) {
  if (typeof start !== 'string' || typeof end !== 'string' || !Number.isFinite(Date.parse(start)) ||
      !Number.isFinite(Date.parse(end)) || Date.parse(end) <= Date.parse(start)) throw new Error('Invalid rental dates.');
  return {startDate:start, predictedEndDate:end};
}
type Attribute = {key?: string; value?: {stringValue?: string}};
type RawSpan = {spanId?: string; parentSpanId?: string; name?: string; startTimeUnixNano?: string; endTimeUnixNano?: string; status?: {code?: number}};
type ResourceSpans = {resource?: {attributes?: Attribute[]}; scopeSpans?: {spans?: RawSpan[]}[]; instrumentationLibrarySpans?: {spans?: RawSpan[]}[]};
export function traceSpans(trace: {batches?: ResourceSpans[]; resourceSpans?: ResourceSpans[]}): Span[] {
  const spans = (trace.batches ?? trace.resourceSpans ?? []).flatMap(batch => {
    const service = batch.resource?.attributes?.find(a => a.key === 'service.name')?.value?.stringValue ?? 'unknown';
    return (batch.scopeSpans ?? batch.instrumentationLibrarySpans ?? []).flatMap(scope => (scope.spans ?? []).map(span => ({
      id:span.spanId ?? '', parentId:span.parentSpanId ?? '', service, name:span.name ?? '',
      start:Number(span.startTimeUnixNano)/1e6,
      duration:(Number(span.endTimeUnixNano)-Number(span.startTimeUnixNano))/1e6, error:span.status?.code === 2
    })));
  });
  return spans.filter(s => Number.isFinite(s.start) && Number.isFinite(s.duration)).sort((a,b) => a.start-b.start);
}
