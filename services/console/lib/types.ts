export type Rental = { rentalId: string; userId: string; motorcycleId: string; motorcycleLicencePlate: string;
  startDate: string; predictedEndDate: string; actualEndDate: string | null; originalTotalCost: number };
export type RentalPage = {items: Rental[]; nextCursor: string | null};
export type Position = {latitude: number; longitude: number; recorded_at: number};
export type Span = {id: string; parentId: string; service: string; name: string; start: number; duration: number; error: boolean};
export type Attempt = {index: number; plate: string; status: number; duration: number; traceId: string; grant: string; detail: string};
export type Metrics = {p99: number | null; queue: number | null; limited: number | null; measuredAt: string};
