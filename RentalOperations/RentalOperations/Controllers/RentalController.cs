using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalOperations.DTOs;
using RentalOperations.Services;
using RentalOperations.Filters;
using System.Security.Claims;

namespace RentalOperations.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RentalController : ControllerBase
    {
        private readonly IRentalService _rentalService;

        public RentalController(IRentalService rentalService)
        {
            _rentalService = rentalService;
        }

        [Authorize]
        [ServiceFilter(typeof(AuthorizationFilter))]
        [HttpPost("create")]
        public async Task<IActionResult> CreateRental([FromBody] RentalCreateDto createDto)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
                if(userIdClaim == null)
                {
                    return Forbid();
                }
                await _rentalService.CreateRentalAsync(createDto, userIdClaim.Value);
                return Ok("Created with Success!");
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [Authorize]
        [ServiceFilter(typeof(AuthorizationFilter))]
        [HttpGet("user")]
        public async Task<IActionResult> GetRentalsByUser()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
                if (userIdClaim == null)
                {
                    return Forbid();
                }
                var rentals = await _rentalService.GetRentalsByUserIdAsync(userIdClaim.Value);
                if (rentals == null || rentals.Count == 0)
                    return NotFound($"No rentals found for user ID {userIdClaim.Value}");

                return Ok(rentals);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [Authorize]
        [ServiceFilter(typeof(AdminAuthorizationFilter))]
        [HttpGet("user/{userId}")]
        public async Task<IActionResult> GetRentalsByUserAdmin(string userId)
        {
            try
            {
                var rentals = await _rentalService.GetRentalsByUserIdAsync(userId);
                if (rentals == null || rentals.Count == 0)
                    return NotFound($"No rentals found for user ID {userId}");

                return Ok(rentals);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [Authorize]
        [ServiceFilter(typeof(AuthorizationFilter))]
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
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [ServiceFilter(typeof(AuthorizationFilter))]
        [HttpGet("is-rented/{licencePlate}")]
        public async Task<IActionResult> IsMotorcycleRented(string licencePlate)
        {
            try
            {
                bool isRented = await _rentalService.IsMotorcycleCurrentlyRentedAsync(licencePlate);
                return Ok(isRented);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error checking rental status: {ex.Message}");
            }
        }
    }
}
