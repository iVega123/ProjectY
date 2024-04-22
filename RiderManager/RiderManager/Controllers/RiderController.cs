using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RiderManager.DTOs;
using RiderManager.Filters;
using RiderManager.Managers;
using System.Security.Claims;

namespace RiderManager.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class RidersController : ControllerBase
    {
        private readonly IRiderManager _riderManager;

        public RidersController(IRiderManager riderManager)
        {
            _riderManager = riderManager;
        }

        [Authorize]
        [ServiceFilter(typeof(AdminAuthorizationFilter))]
        [HttpGet]
        public async Task<IActionResult> GetAllRiders()
        {
            var riders = await _riderManager.GetAllRidersAsync();
            return Ok(riders);
        }


        [ServiceFilter(typeof(AdminAuthorizationFilter))]
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

        [Authorize]
        [ServiceFilter(typeof(AdminAuthorizationFilter))]
        [HttpDelete("{userId}")]
        public async Task<IActionResult> DeleteRider(string userId)
        {
            await _riderManager.DeleteRiderAsync(userId);
            return NoContent();
        }

        [Authorize]
        [ServiceFilter(typeof(AuthorizationFilter))]
        [HttpPut("/update-image")]
        public async Task<IActionResult> UpdateRiderCNH(IFormFile cnhFile)
        {
            var userId = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;

            var role = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Role)?.Value;

            if (role.Contains("Admin"))
            {
                return Unauthorized("Only Riders can Update their Photos...");
            }

            if (userId == null)
            {
                return Unauthorized("Token Invalid");
            }
            await _riderManager.UpdateRiderImageAsync(userId, cnhFile);
            return Ok("CNH Photo updated");
        }
    }
}
