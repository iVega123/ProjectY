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

        private ObjectResult Unavailable(Exception exception)
        {
            DependencyFailure.Record(exception);
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
            catch (Exception ex) when (DependencyFailure.IsUnavailable(ex))
            {
                return Unavailable(ex);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("is-rented/{licencePlate}")]
        public async Task<IActionResult> IsMotorcycleRented(string licencePlate)
        {
            try
            {
                bool isRented = await _rentalService.IsMotorcycleCurrentlyRentedAsync(licencePlate);
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

        [Authorize(Roles = "Admin")]
        [HttpPost("motorcycle-retirements/{licencePlate}")]
        public async Task<IActionResult> TryRetireMotorcycle(string licencePlate)
        {
            var acquired = await _rentalService.TryRetireMotorcycleAsync(licencePlate);
            return acquired
                ? NoContent()
                : Conflict(new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Active rental conflict",
                    Detail = $"Motorcycle {licencePlate} has an active rental."
                });
        }

        [Authorize(Roles = "Admin")]
        [HttpPost("motorcycle-renames/reservations")]
        public async Task<IActionResult> TryReserveMotorcycleRename(
            [FromBody] MotorcycleRenameReservationDto request)
        {
            var acquired = await _rentalService.TryReserveLicensePlateRenameAsync(
                request.OldLicencePlate,
                request.NewLicencePlate);
            return acquired
                ? NoContent()
                : Conflict(new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Motorcycle claim conflict",
                    Detail = $"Motorcycle {request.NewLicencePlate} is already claimed."
                });
        }
    }
}
