using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MotoHub.DTOs;
using MotoHub.Filters;
using MotoHub.Services;

namespace MotoHub.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MotorcyclesController : ControllerBase
    {
        private readonly IMotorcycleService _motorcycleService;

        private readonly ILogger<MotorcyclesController> _logger;

        public MotorcyclesController(IMotorcycleService motorcycleService, ILogger<MotorcyclesController> logger)
        {
            _motorcycleService = motorcycleService;
            _logger = logger;
        }

        [Authorize]
        [ServiceFilter(typeof(AdminAuthorizationFilter))]
        [HttpGet]
        public IActionResult GetAll()
        {
            _logger.LogInformation("Fetching all motorcycles.");
            var motorcycles = _motorcycleService.GetAllMotorcycles();
            return Ok(motorcycles);
        }

        [ServiceFilter(typeof(AdminAuthorizationFilter))]
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

        [Authorize]
        [ServiceFilter(typeof(AdminAuthorizationFilter))]
        [HttpPost]
        public IActionResult Create([FromBody]MotorcycleDTO motorcycle)
        {
            _logger.LogInformation("Creating motorcycle with license plate {LicensePlate}.", motorcycle.LicensePlate);
            if (_motorcycleService.LicensePlateExists(motorcycle.LicensePlate))
            {
                _logger.LogWarning("License plate {LicensePlate} already exists.", motorcycle.LicensePlate);
                return Conflict("License plate already exists.");
            }

            _motorcycleService.CreateMotorcycle(motorcycle);
            return Ok("Created!");
        }

        [Authorize]
        [ServiceFilter(typeof(AdminAuthorizationFilter))]
        [HttpPut("{licensePlate}")]
        public async Task<IActionResult> Update(string licensePlate, string newLicencePlate)
        {
            _logger.LogInformation("Updating motorcycle with license plate {LicensePlate}.", licensePlate);
            var existingMotorcycle = await _motorcycleService.GetMotorcycleByLicensePlateAsync(licensePlate);
            if (existingMotorcycle == null)
            {
                _logger.LogWarning("Motorcycle with license plate {LicensePlate} not found.", licensePlate);
                return NotFound();
            }

            await _motorcycleService.UpdateMotorcycleAsync(licensePlate, newLicencePlate);
            return NoContent();
        }

        [Authorize]
        [ServiceFilter(typeof(AdminAuthorizationFilter))]
        [HttpDelete("{licensePlate}")]
        public async Task<IActionResult> Delete(string licensePlate)
        {
            _logger.LogInformation("Deleting motorcycle with license plate {LicensePlate}.", licensePlate);

            var result = await _motorcycleService.DeleteMotorcycle(licensePlate);
            if(result.Success)
            {
                return Ok("Deleted Successfully");
            
            } else {
                
                return BadRequest(result.Message); 
            }
            
        }
    }
}
