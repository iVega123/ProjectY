use axum::http::Method;

use crate::config::UpstreamName;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Access {
    Public,
    Authenticated,
    Admin,
}

pub fn is_canonical_path(path: &str) -> bool {
    if path.len() > 1 && path.ends_with('/') {
        return false;
    }
    if path.contains('%') || path.contains('\\') || path.contains("//") {
        return false;
    }
    !path.split('/').any(|segment| matches!(segment, "." | ".."))
}

pub fn requires_revocation_check(method: &Method, path: &str, upstream: UpstreamName) -> bool {
    upstream == UpstreamName::RentalOperations
        && method == Method::POST
        && path.eq_ignore_ascii_case("/api/rental/create")
}

pub fn access_for(method: &Method, path: &str, upstream: UpstreamName) -> Access {
    let path = path.to_ascii_lowercase();
    match upstream {
        UpstreamName::AuthGate
            if method == Method::POST
                && matches!(
                    path.as_str(),
                    "/api/auth/login" | "/api/auth/register/rider"
                ) =>
        {
            Access::Public
        }
        UpstreamName::MotoHub if motorcycle_read_route(method, &path) => Access::Authenticated,
        UpstreamName::MotoHub => Access::Admin,
        UpstreamName::RiderManager if rider_admin_route(method, &path) => Access::Admin,
        UpstreamName::RentalOperations if rental_admin_route(method, &path) => Access::Admin,
        // O billing não tem API de escrita, e o portão não deveria fingir que
        // tem. Qualquer coisa que não seja leitura para lá só faz sentido vinda
        // de um administrador -- e hoje não faz sentido nenhum.
        UpstreamName::Billing if method != Method::GET => Access::Admin,
        _ => Access::Authenticated,
    }
}

/// Ler uma moto -- uma, pelo identificador dela -- é o que um piloto precisa
/// para alugá-la, e o serviço já tratava assim: GetByLicensePlate nunca teve
/// [Authorize(Roles = "Admin")]. Quem recusava era este portão, com um Admin só
/// para todo o MotoHub, e as duas camadas discordavam em silêncio.
///
/// O catálogo inteiro continua Admin. A diferença entre "esta moto" e "todas as
/// motos" é a diferença entre alugar uma e inventariar a frota.
fn motorcycle_read_route(method: &Method, path: &str) -> bool {
    method == Method::GET
        && path
            .strip_prefix("/api/motorcycles/")
            .is_some_and(|id| !id.is_empty() && !id.contains('/'))
}

fn rider_admin_route(method: &Method, path: &str) -> bool {
    if method == Method::GET && path == "/api/riders" {
        return true;
    }
    let Some(id) = path.strip_prefix("/api/riders/") else {
        return false;
    };
    !id.is_empty() && !id.contains('/') && (method == Method::GET || method == Method::DELETE)
}

// A aposentadoria e a reserva de renomeação eram rotas de um protocolo entre
// dois bancos, e saíram com ele em #135. O portão continuaria concedendo Admin a
// caminhos que ninguém serve -- um contrato falso, que se lê como promessa.
fn rental_admin_route(method: &Method, path: &str) -> bool {
    method == Method::GET
        && path
            .strip_prefix("/api/rental/user/")
            .is_some_and(|id| !id.is_empty() && !id.contains('/'))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_login_and_registration_are_public() {
        assert_eq!(
            access_for(&Method::POST, "/api/auth/login", UpstreamName::AuthGate),
            Access::Public
        );
        assert_eq!(
            access_for(&Method::POST, "/api/auth/logout", UpstreamName::AuthGate),
            Access::Authenticated
        );
    }

    #[test]
    fn maps_legacy_admin_routes_at_the_edge() {
        assert_eq!(
            access_for(
                &Method::GET,
                "/api/riders/user-1",
                UpstreamName::RiderManager
            ),
            Access::Admin
        );
        assert_eq!(
            access_for(
                &Method::GET,
                "/api/rental/user/rider-1",
                UpstreamName::RentalOperations
            ),
            Access::Admin
        );
        assert_eq!(
            access_for(
                &Method::GET,
                "/api/rental/batch",
                UpstreamName::RentalOperations
            ),
            Access::Authenticated
        );
        assert_eq!(
            access_for(
                &Method::POST,
                "/api/rental/create",
                UpstreamName::RentalOperations
            ),
            Access::Authenticated
        );
        assert_eq!(
            access_for(&Method::GET, "/api/motorcycles", UpstreamName::MotoHub),
            Access::Admin
        );
        assert_eq!(
            access_for(&Method::POST, "/api/motorcycles", UpstreamName::MotoHub),
            Access::Admin
        );
    }

    /// A fatura é do piloto, e ler a própria não exige ser administrador.
    ///
    /// Quem filtra por dono é o billing, com a identidade que este portão
    /// assina -- aqui a decisão é só "precisa estar autenticado". O que o portão
    /// nega é escrita: o billing não tem API de escrita, e rotear uma para lá
    /// fingiria que tem.
    #[test]
    fn a_rider_may_read_invoices_but_not_write_them() {
        assert_eq!(
            access_for(
                &Method::GET,
                "/api/invoices/rental-1",
                UpstreamName::Billing
            ),
            Access::Authenticated
        );
        assert_eq!(
            access_for(&Method::GET, "/api/invoices", UpstreamName::Billing),
            Access::Authenticated
        );
        for method in [Method::POST, Method::PUT, Method::DELETE, Method::PATCH] {
            assert_eq!(
                access_for(&method, "/api/invoices/rental-1", UpstreamName::Billing),
                Access::Admin,
                "{method} on an invoice must not be a rider route"
            );
        }
    }

    #[test]
    fn a_rider_may_read_one_motorcycle_but_not_the_fleet() {
        // Alugar exige saber qual moto se está alugando. Listar a frota, não.
        assert_eq!(
            access_for(
                &Method::GET,
                "/api/motorcycles/ABC1234",
                UpstreamName::MotoHub
            ),
            Access::Authenticated
        );
        assert_eq!(
            access_for(&Method::GET, "/api/motorcycles", UpstreamName::MotoHub),
            Access::Admin
        );
        for method in [Method::PUT, Method::DELETE, Method::POST] {
            assert_eq!(
                access_for(&method, "/api/motorcycles/ABC1234", UpstreamName::MotoHub),
                Access::Admin,
                "{method} on one motorcycle must stay Admin"
            );
        }
    }

    #[test]
    fn rejects_paths_that_an_upstream_could_normalize_differently() {
        assert!(is_canonical_path("/api/rental/user/victim"));
        assert!(!is_canonical_path("/api/rental/us%65r/victim"));
        assert!(!is_canonical_path("/api/rental/user/victim/"));
        assert!(!is_canonical_path("/api//rental/user/victim"));
        assert!(!is_canonical_path("/api/rental/../riders"));
        assert!(!is_canonical_path("/api\\rental\\user\\victim"));
    }

    #[test]
    fn checks_revocation_only_for_high_value_operations() {
        assert!(requires_revocation_check(
            &Method::POST,
            "/api/rental/create",
            UpstreamName::RentalOperations
        ));
        assert!(!requires_revocation_check(
            &Method::GET,
            "/api/rental/user",
            UpstreamName::RentalOperations
        ));
    }
}
