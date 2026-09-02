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
        UpstreamName::MotoHub => Access::Admin,
        UpstreamName::RiderManager if rider_admin_route(method, &path) => Access::Admin,
        UpstreamName::RentalOperations if rental_admin_route(method, &path) => Access::Admin,
        _ => Access::Authenticated,
    }
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

fn rental_admin_route(method: &Method, path: &str) -> bool {
    (method == Method::GET
        && path
            .strip_prefix("/api/rental/user/")
            .is_some_and(|id| !id.is_empty() && !id.contains('/')))
        || (method == Method::POST
            && (path
                .strip_prefix("/api/rental/motorcycle-retirements/")
                .is_some_and(|plate| !plate.is_empty() && !plate.contains('/'))
                || path == "/api/rental/motorcycle-renames/reservations"))
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
                &Method::POST,
                "/api/rental/motorcycle-retirements/ABC1234",
                UpstreamName::RentalOperations
            ),
            Access::Admin
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
            access_for(
                &Method::GET,
                "/api/motorcycles/ABC1234",
                UpstreamName::MotoHub
            ),
            Access::Admin
        );
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
