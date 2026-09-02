using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RiderManager.DTOs;
using RiderManager.Managers;
using System.Security.Claims;

namespace RiderManager.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class RidersController : ControllerBase
    {
        private readonly IRiderManager _riderManager;

        public RidersController(IRiderManager riderManager)
        {
            _riderManager = riderManager;
        }

        [Authorize(Roles = "Admin")]
        [HttpGet]
        public async Task<IActionResult> GetAllRiders(
            [FromQuery] string? cursor,
            [FromQuery] int? pageSize)
        {
            try
            {
                return Ok(await _riderManager.GetRidersAsync(cursor, pageSize));
            }
            catch (FormatException exception)
            {
                return BadRequest(exception.Message);
            }
        }


        [HttpGet("{userId}")]
        public async Task<IActionResult> GetRiderByUserId(string userId)
        {
            var rider = await _riderManager.GetRiderByUserIdAsync(userId);
            if (rider == null)
            {
                return NotFound($"Rider with UserId {userId} not found.");
            }
            return Ok(rider);
        }

        [Authorize(Roles = "Admin")]
        [HttpDelete("{userId}")]
        public async Task<IActionResult> DeleteRider(string userId)
        {
            await _riderManager.DeleteRiderAsync(userId);
            return NoContent();
        }

        [Authorize(Roles = "Rider")]
        [HttpPut("/update-image")]
        public async Task<IActionResult> UpdateRiderCNH(IFormFile cnhFile)
        {
            var userId = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;

            if (userId == null)
            {
                return Unauthorized("Token Invalid");
            }
            await _riderManager.UpdateRiderImageAsync(userId, cnhFile);
            return Ok("CNH Photo updated");
        }
    }
}
