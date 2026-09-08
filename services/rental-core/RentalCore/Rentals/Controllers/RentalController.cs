using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalOperations.DTOs;
using RentalOperations.Services;
using RentalOperations.Domain;
using System.Security.Claims;

namespace RentalOperations.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class RentalController : ControllerBase
    {
        private readonly IRentalService _rentalService;

        private static ProblemDetails Problem400(string detail) => new()
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Invalid batch request",
            Detail = detail
        };

        private ObjectResult Unavailable(Exception exception)
        {
            DependencyFailure.Record(exception);
            if (exception is PreWriteDependencyException)
            {
                ProjectY.Shared.Idempotency.RedisIdempotencyMiddleware.AllowRetryBeforeSideEffects(HttpContext);
            }
            Response.Headers.RetryAfter = "1";
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Dependency unavailable",
                Detail = "A required dependency is temporarily unavailable. Retry later."
            });
        }
        public RentalController(IRentalService rentalService)
        {
            _rentalService = rentalService;
        }

        [HttpPost("create")]
        public async Task<IActionResult> CreateRental([FromBody] RentalCreateDto createDto)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
                if (userIdClaim == null)
                {
                    return Forbid();
                }
                await _rentalService.CreateRentalAsync(createDto, userIdClaim.Value);
                return Ok("Created with Success!");
            }
            catch (ActiveRentalConflictException ex)
            {
                return Conflict(new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Active rental conflict",
                    Detail = ex.Message
                });
            }
            catch (MotorcycleRetiredException ex)
            {
                return Conflict(new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Motorcycle retired",
                    Detail = ex.Message
                });
            }
            catch (Exception ex) when (DependencyFailure.IsUnavailable(ex))
            {
                return Unavailable(ex);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("user")]
        public async Task<IActionResult> GetRentalsByUser(
            [FromQuery] string? cursor,
            [FromQuery] int? pageSize)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
                if (userIdClaim == null)
                {
                    return Forbid();
                }
                return Ok(await _rentalService.GetRentalsByUserIdAsync(
                    userIdClaim.Value,
                    cursor,
                    pageSize));
            }
            catch (Exception ex) when (DependencyFailure.IsUnavailable(ex))
            {
                return Unavailable(ex);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [Authorize(Roles = "Admin")]
        [HttpGet("user/{userId}")]
        public async Task<IActionResult> GetRentalsByUserAdmin(
            string userId,
            [FromQuery] string? cursor,
            [FromQuery] int? pageSize)
        {
            try
            {
                return Ok(await _rentalService.GetRentalsByUserIdAsync(userId, cursor, pageSize));
            }
            catch (Exception ex) when (DependencyFailure.IsUnavailable(ex))
            {
                return Unavailable(ex);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost("calculate-final-cost")]
        public async Task<IActionResult> CalculateFinalCost([FromQuery] string rentalId, [FromQuery] DateTime actualEndDate)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
                if (userIdClaim == null)
                {
                    return Forbid();
                }
                var response = await _rentalService.CalculateFinalCostAsync(rentalId, userIdClaim.Value, actualEndDate);
                return Ok(response);
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
            catch (RentalSettlementConflictException ex)
            {
                return Conflict(new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Rental settlement conflict",
                    Detail = ex.Message
                });
            }
            catch (Exception ex) when (DependencyFailure.IsUnavailable(ex))
            {
                return Unavailable(ex);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        /// Leitura em lote, para que compor uma tela não custe uma requisição
        /// por linha.
        ///
        /// O #138 trata isto como contrato, não como otimização: sem o lote o
        /// N+1 não desaparece, apenas se muda do navegador para o BFF. O teto
        /// existe porque um lote ilimitado é uma consulta arbitrária escrita
        /// pelo cliente.
        /// </summary>
        [HttpGet("batch")]
        public async Task<IActionResult> GetRentalsByIds([FromQuery] string? ids)
        {
            var requested = (ids ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (requested.Length == 0)
            {
                return BadRequest(Problem400("At least one rental id is required."));
            }
            if (requested.Length > RentalService.MaxBatchSize)
            {
                return BadRequest(Problem400(
                    $"A batch may request at most {RentalService.MaxBatchSize} rental ids."));
            }

            var parsed = new List<Guid>(requested.Length);
            foreach (var candidate in requested)
            {
                if (!Guid.TryParse(candidate, out var id))
                {
                    return BadRequest(Problem400($"'{candidate}' is not a rental id."));
                }
                parsed.Add(id);
            }

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
                if (userIdClaim == null)
                {
                    return Forbid();
                }
                return Ok(await _rentalService.GetRentalsByIdsAsync(
                    parsed,
                    userIdClaim.Value,
                    User.IsInRole("Admin")));
            }
            catch (Exception ex) when (DependencyFailure.IsUnavailable(ex))
            {
                return Unavailable(ex);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("is-rented/{motorcycleId:guid}")]
        public async Task<IActionResult> IsMotorcycleRented(Guid motorcycleId)
        {
            try
            {
                bool isRented = await _rentalService.IsMotorcycleCurrentlyRentedAsync(motorcycleId);
                return Ok(isRented);
            }
            catch (Exception ex) when (DependencyFailure.IsUnavailable(ex))
            {
                return Unavailable(ex);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error checking rental status: {ex.Message}");
            }
        }

    }
}
