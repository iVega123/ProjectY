using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MotoHub.DTOs;
using MotoHub.Services;
using ProjectY.Shared.Validation;

namespace MotoHub.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class MotorcyclesController : ControllerBase
    {
        private readonly IMotorcycleService _motorcycleService;

        private readonly ILogger<MotorcyclesController> _logger;

        public MotorcyclesController(IMotorcycleService motorcycleService, ILogger<MotorcyclesController> logger)
        {
            _motorcycleService = motorcycleService;
            _logger = logger;
        }

        [Authorize(Roles = "Admin")]
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] string? cursor, [FromQuery] int? pageSize)
        {
            _logger.LogInformation("Fetching a page of motorcycles.");
            try
            {
                return Ok(await _motorcycleService.GetMotorcyclesAsync(cursor, pageSize));
            }
            catch (FormatException exception)
            {
                return BadRequest(exception.Message);
            }
        }

        [HttpGet("{licensePlate}")]
        public async Task<IActionResult> GetByLicensePlateAsync(string licensePlate)
        {
            _logger.LogInformation("Fetching motorcycle by license plate: {LicensePlate}", licensePlate);
            var motorcycle = await _motorcycleService.GetMotorcycleByLicensePlateAsync(licensePlate);
            if (motorcycle == null)
            {
                _logger.LogWarning("Motorcycle with license plate {LicensePlate} not found.", licensePlate);
                return NotFound();
            }
            return Ok(motorcycle);
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        public IActionResult Create([FromBody] MotorcycleDTO motorcycle)
        {
            motorcycle.LicensePlate = BrazilianLicensePlateAttribute.Normalize(motorcycle.LicensePlate);
            motorcycle.Model = motorcycle.Model?.Trim();
            _logger.LogInformation("Creating motorcycle with license plate {LicensePlate}.", motorcycle.LicensePlate);
            if (_motorcycleService.LicensePlateExists(motorcycle.LicensePlate))
            {
                _logger.LogWarning("License plate {LicensePlate} already exists.", motorcycle.LicensePlate);
                return Conflict("License plate already exists.");
            }

            _motorcycleService.CreateMotorcycle(motorcycle);
            return Ok("Created!");
        }

        [Authorize(Roles = "Admin")]
        [HttpPut("{licensePlate}")]
        public async Task<IActionResult> Update(string licensePlate, string newLicencePlate)
        {
            licensePlate = BrazilianLicensePlateAttribute.Normalize(licensePlate);
            newLicencePlate = BrazilianLicensePlateAttribute.Normalize(newLicencePlate);
            _logger.LogInformation("Updating motorcycle with license plate {LicensePlate}.", licensePlate);
            var existingMotorcycle = await _motorcycleService.GetMotorcycleByLicensePlateAsync(licensePlate);
            if (existingMotorcycle == null)
            {
                _logger.LogWarning("Motorcycle with license plate {LicensePlate} not found.", licensePlate);
                return NotFound();
            }

            try
            {
                await _motorcycleService.UpdateMotorcycleAsync(licensePlate, newLicencePlate);
                return NoContent();
            }
            catch (InvalidOperationException exception)
            {
                return Conflict(exception.Message);
            }
        }

        [Authorize(Roles = "Admin")]
        [HttpDelete("{licensePlate}")]
        public async Task<IActionResult> Delete(string licensePlate)
        {
            _logger.LogInformation("Deleting motorcycle with license plate {LicensePlate}.", licensePlate);

            var result = await _motorcycleService.DeleteMotorcycle(licensePlate);
            if (result.Success)
            {
                return Ok("Deleted Successfully");

            }
            else
            {
                return result.StatusCode is { } statusCode
                    ? StatusCode(statusCode, result.Message)
                    : BadRequest(result.Message);
            }

        }

        [HttpPost("historical-references")]
        public async Task<IActionResult> EnsureHistoricalReferences(
            [FromBody] HistoricalMotorcycleReferencesRequest request)
        {
            await _motorcycleService.EnsureHistoricalReferencesAsync(request.LicensePlates);
            return NoContent();
        }
    }
}
