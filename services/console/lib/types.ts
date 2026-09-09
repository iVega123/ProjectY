export type Rental = { rentalId: string; userId: string; motorcycleId: string; motorcycleLicencePlate: string;
  startDate: string; predictedEndDate: string; actualEndDate: string | null; originalTotalCost: number };
export type RentalPage = {items: Rental[]; nextCursor: string | null};
// Os campos abaixo são os que contracts/reads.json declara, e nada além. Um
// campo a mais aqui é um campo que a tela usa sem ter pedido.
export type Motorcycle = {id: string; model: string | null; year: number; licensePlate: string};
export type Invoice = {rentalId: string; currency: string; totalMinor: number;
  adjustmentMinor: number; reason: string; daysUsed: number};
export type RiderCard = {userId: string; name: string; cnhType: string; verified: boolean};
// O aluguel com o que os outros serviços sabem sobre ele. Ausente quer dizer
// "não chegou", e a tela mostra a linha assim mesmo -- ver ADR 0014.
export type Composed = Rental & {motorcycle: Motorcycle | null; invoice: Invoice | null};
export type ComposedPage = {items: Composed[]; nextCursor: string | null;
  rider: RiderCard | null; missing: string[]};
export type Position = {latitude: number; longitude: number; recorded_at: number};
export type Span = {id: string; parentId: string; service: string; name: string; start: number; duration: number; error: boolean};
export type Attempt = {index: number; plate: string; status: number; duration: number; traceId: string; grant: string; detail: string};
export type Metrics = {p99: number | null; queue: number | null; limited: number | null; measuredAt: string};
