# Pagination growth proof

Issue #98 makes every public collection endpoint cursor-paged with a server-side maximum of 100 rows. The database plans are guarded by integration tests:

- MotoHub lists active motorcycles through `IX_Motorcycles_Active_Id`.
- RiderManager walks the rider primary key and joins the stored CNH URL in the same query.
- RentalOperations lists a user's rentals through `ix_rentals_user_cursor`; motorcycle availability and overlap predicates run inside MongoDB and are index-backed.

Run the growth check through the root Compose gateway with a local k6 binary
after obtaining an admin access token for the MotoHub audience from the identity
issuer delivered by issue #136:

```powershell
$env:ADMIN_TOKEN = '<admin-jwt>'
k6 run load/k6/pagination-growth.js
```

The script seeds a baseline, measures its p99, adds 2,000 rows, then measures the same bounded first-page query under load. It fails when fewer than 99% of grown-dataset requests stay within `MAX_P99_GROWTH_RATIO` (default `1.50`) plus a small jitter allowance. `BASELINE_RECORDS`, `GROWTH_RECORDS`, `BASELINE_SAMPLES`, `VUS`, and `DURATION` are configurable environment variables.
