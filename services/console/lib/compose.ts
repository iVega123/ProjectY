import type { Composed, Invoice, Motorcycle, Rental, RiderCard } from './types';

/**
 * A composição da tela de aluguéis.
 *
 * O ADR 0014 põe isto no BFF e não no portão, e a razão é a taxa de mudança: o
 * portão muda raramente e falha FECHADO porque é a fronteira de segurança;
 * compor uma leitura muda toda vez que a tela muda e falha SUAVE -- renderiza o
 * que chegou. Misturar os dois faria cada campo novo numa tela virar um deploy
 * do que valida token.
 *
 * O padrão DataLoader sobrevive; a tecnologia não. O que ele realmente faz é
 * juntar as chaves de uma requisição numa CHAMADA só -- e isso exige um
 * endpoint de lote do outro lado, que é por que o #138 trata os lotes como
 * contrato e não como otimização. Sem eles o N+1 não desaparece: sai do
 * navegador e entra aqui.
 */

/** O teto de um lote. É o mesmo tamanho de página que o console pede. */
export const MAX_BATCH = 100;

/** Quantos aluguéis uma página traz -- e, por isso, quantos ids um lote recebe. */
export const PAGE_SIZE = 100;

/**
 * As chamadas que compor uma página custa, além da própria página.
 *
 * É um número, e não um limite: o teste conta as chamadas com 1 linha e com
 * PAGE_SIZE linhas e exige o mesmo resultado. É assim que "bounded, independent
 * of how many rows are on it" deixa de ser afirmação e vira medida.
 */
export const COMPOSITION_CALLS = 3;

type Fetch = (path: string) => Promise<Response>;

/** Ids distintos, na ordem em que apareceram, sem os vazios. */
export function distinct(values: (string | null | undefined)[]): string[] {
  return [...new Set(values.filter((value): value is string => !!value))];
}

/**
 * O resultado de uma chamada da composição.
 *
 * `reached` separa "o serviço respondeu que não há" de "o serviço não
 * respondeu". As duas produzem uma linha sem o campo, e só a segunda é uma
 * degradação que a tela deve anunciar. Um administrador não tem registro de
 * piloto: dizer "sem piloto" para ele estaria certo; dizer "o identity caiu"
 * seria mentira, e mentira que aparece toda vez.
 */
type Reply<T> = {value: T; reached: boolean};

async function collect<T>(call: Fetch, path: string): Promise<Reply<T[]>> {
  try {
    const response = await call(path);
    if (!response.ok) return {value: [], reached: false};
    const body = await response.json();
    return Array.isArray(body) ? {value: body as T[], reached: true} : {value: [], reached: false};
  } catch {
    return {value: [], reached: false};
  }
}

async function one<T>(call: Fetch, path: string): Promise<Reply<T | null>> {
  try {
    const response = await call(path);
    // 404 é resposta: este identificador não tem registro. Ver Reply.
    if (response.status === 404) return {value: null, reached: true};
    if (!response.ok) return {value: null, reached: false};
    return {value: (await response.json()) as T, reached: true};
  } catch {
    return {value: null, reached: false};
  }
}

function index<T>(rows: T[], key: (row: T) => string): Map<string, T> {
  return new Map(rows.map(row => [key(row), row]));
}

/**
 * Compõe uma página de aluguéis.
 *
 * Três chamadas, em paralelo, seja a página de uma linha ou de cem:
 *
 *   1. as motos dos aluguéis     rental-core   lote por id
 *   2. as notas dos aluguéis     billing       lote por rental_id
 *   3. o piloto que está logado  identity      leitura única
 *
 * A terceira é única e não em lote de propósito: a tela mostra os aluguéis de
 * QUEM PEDIU, então o conjunto de pilotos de uma página tem sempre tamanho um.
 * Chamar o lote para um id só seria pedir a rota de administrador para uma
 * pergunta que a rota do próprio dono responde.
 */
export async function compose(
  rentals: Rental[],
  callerId: string,
  call: Fetch,
): Promise<{ items: Composed[]; rider: RiderCard | null; missing: string[] }> {
  const motorcycleIds = distinct(rentals.map(rental => rental.motorcycleId));
  const rentalIds = distinct(rentals.map(rental => rental.rentalId));

  // Sem linha nenhuma não há nada a compor, e um lote vazio é 400 do outro
  // lado. Continuar traria só o piloto -- que a tela mostra mesmo vazia.
  const [motorcycles, invoices, rider] = await Promise.all([
    motorcycleIds.length
      ? collect<Motorcycle>(call, '/api/motorcycles/batch?ids=' + ids(motorcycleIds))
      : Promise.resolve<Reply<Motorcycle[]>>({value: [], reached: true}),
    rentalIds.length
      ? collect<Invoice>(call, '/api/invoices?rentalIds=' + ids(rentalIds))
      : Promise.resolve<Reply<Invoice[]>>({value: [], reached: true}),
    one<RiderCard>(call, '/api/riders/' + encodeURIComponent(callerId)),
  ]);

  const byMotorcycle = index(motorcycles.value, motorcycle => motorcycle.id);
  const byRental = index(invoices.value, invoice => invoice.rentalId);

  const missing: string[] = [];
  if (!motorcycles.reached) missing.push('motorcycles');
  if (!invoices.reached) missing.push('invoices');
  if (!rider.reached) missing.push('rider');

  return {
    items: rentals.map(rental => ({
      ...rental,
      motorcycle: byMotorcycle.get(rental.motorcycleId) ?? null,
      invoice: byRental.get(rental.rentalId) ?? null,
    })),
    rider: rider.value,
    missing,
  };
}

/**
 * Os ids de um lote, cortados no teto.
 *
 * O corte não deveria acontecer: a página pede PAGE_SIZE e o lote aceita
 * MAX_BATCH, que são o mesmo número por acordo entre os dois lados. O corte
 * existe para que subir um dos dois sem o outro produza uma tela incompleta em
 * vez de um 400 que apaga a página inteira -- e há teste fixando a igualdade,
 * para que subir um dos dois sem o outro fique vermelho antes disso.
 */
function ids(values: string[]) {
  return values.slice(0, MAX_BATCH).map(encodeURIComponent).join(',');
}
