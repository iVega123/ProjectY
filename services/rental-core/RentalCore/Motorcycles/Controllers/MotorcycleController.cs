using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MotoHub.DTOs;
using MotoHub.Services;
using RentalCore.Errors;
using ProjectY.Shared.Validation;

namespace MotoHub.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public partial class MotorcyclesController : ControllerBase
    {
        private readonly IMotorcycleService _motorcycleService;

        private readonly ILogger<MotorcyclesController> _logger;

        private BadRequestObjectResult Invalid(string detail) =>
            BadRequest(ProblemFactory.Create(
                HttpContext, StatusCodes.Status400BadRequest,
                ProblemTypes.InvalidRequest, "Invalid request", detail));

        public MotorcyclesController(IMotorcycleService motorcycleService, ILogger<MotorcyclesController> logger)
        {
            _motorcycleService = motorcycleService;
            _logger = logger;
        }

        [Authorize(Roles = "Admin")]
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] string? cursor, [FromQuery] int? pageSize)
        {
            LogFetchingPage(_logger);
            return Ok(await Cursors.Paged(
                () => _motorcycleService.GetMotorcyclesAsync(cursor, pageSize)));
        }

        /// <summary>
        /// A moto pelo identificador dela.
        ///
        /// A referência do aluguel passou a ser o id em #134, então a leitura
        /// precisa aceitar o id -- senão quem tem um aluguel na mão não consegue
        /// descobrir de que moto ele é sem passar pela placa, que é justamente o
        /// caminho que deixou de ser a referência.
        ///
        /// A restrição :guid é o que separa esta rota da de placa. Nenhuma placa
        /// brasileira se parece com um UUID, então não há ambiguidade a resolver.
        /// </summary>
        /// <summary>
        /// As motos de uma tela, numa chamada.
        ///
        /// Existe por contrato, e não por desempenho. O aluguel passou a
        /// referenciar a moto pelo id em #134 e carrega apenas a placa; modelo e
        /// ano moram aqui. Sem o lote, compor uma página de aluguéis custa uma
        /// requisição por linha -- o N+1 não desaparece, apenas se muda do
        /// navegador para o BFF, que é exatamente o que o ADR 0014 recusa.
        ///
        /// Não é rota de administrador. Ela é N vezes "esta moto", e ler uma moto
        /// já é do piloto que vai alugá-la; o catálogo inteiro é que continua
        /// sendo inventário da frota.
        ///
        /// O segmento literal `batch` ganha da rota de placa na precedência do
        /// roteamento, e nenhuma placa brasileira se parece com a palavra.
        /// </summary>
        [HttpGet("batch")]
        public async Task<IActionResult> GetByIdsAsync([FromQuery] string? ids)
        {
            var requested = (ids ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (requested.Length == 0)
            {
                return Invalid("At least one motorcycle id is required.");
            }
            if (requested.Length > MotorcycleService.MaxBatchSize)
            {
                return Invalid(
                    $"A batch may request at most {MotorcycleService.MaxBatchSize} motorcycle ids.");
            }

            var parsed = new List<Guid>(requested.Length);
            foreach (var candidate in requested)
            {
                if (!Guid.TryParse(candidate, out var id))
                {
                    // O identificador recusado NÃO volta na resposta: ele é
                    // entrada do cliente, e devolvê-lo é um refletor pronto.
                    return Invalid("Every motorcycle id must be a UUID.");
                }
                parsed.Add(id);
            }

            // Um id ausente vira ausência na lista, e não 404: uma tela compõe o
            // que chegou, e uma moto que saiu do catálogo não pode apagar as
            // outras linhas da página.
            return Ok(await _motorcycleService.GetMotorcyclesByIdsAsync(parsed));
        }

        [HttpGet("{id:guid}")]
        public async Task<IActionResult> GetByIdAsync(Guid id)
        {
            var motorcycle = await _motorcycleService.GetMotorcycleByIdAsync(id);
            return motorcycle == null ? NotFound() : Ok(motorcycle);
        }

        [HttpGet("{licensePlate}")]
        public async Task<IActionResult> GetByLicensePlateAsync(string licensePlate)
        {
            LogFetchingByLicensePlate(_logger, licensePlate);
            var motorcycle = await _motorcycleService.GetMotorcycleByLicensePlateAsync(licensePlate);
            if (motorcycle == null)
            {
                LogLicensePlateNotFound(_logger, licensePlate);
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
            LogCreating(_logger, motorcycle.LicensePlate);
            if (_motorcycleService.LicensePlateExists(motorcycle.LicensePlate))
            {
                LogLicensePlateExists(_logger, motorcycle.LicensePlate);
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
            LogUpdating(_logger, licensePlate);
            var existingMotorcycle = await _motorcycleService.GetMotorcycleByLicensePlateAsync(licensePlate);
            if (existingMotorcycle == null)
            {
                LogLicensePlateNotFound(_logger, licensePlate);
                return NotFound();
            }

            await _motorcycleService.UpdateMotorcycleAsync(licensePlate, newLicencePlate);
            return NoContent();
        }

        [Authorize(Roles = "Admin")]
        [HttpDelete("{licensePlate}")]
        public async Task<IActionResult> Delete(string licensePlate)
        {
            LogDeleting(_logger, licensePlate);

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

        [LoggerMessage(Level = LogLevel.Information, Message = "Fetching a page of motorcycles.")]
        private static partial void LogFetchingPage(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Fetching motorcycle by license plate: {LicensePlate}")]
        private static partial void LogFetchingByLicensePlate(ILogger logger, string? licensePlate);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Motorcycle with license plate {LicensePlate} not found.")]
        private static partial void LogLicensePlateNotFound(ILogger logger, string? licensePlate);

        [LoggerMessage(Level = LogLevel.Information, Message = "Creating motorcycle with license plate {LicensePlate}.")]
        private static partial void LogCreating(ILogger logger, string? licensePlate);

        [LoggerMessage(Level = LogLevel.Warning, Message = "License plate {LicensePlate} already exists.")]
        private static partial void LogLicensePlateExists(ILogger logger, string? licensePlate);

        [LoggerMessage(Level = LogLevel.Information, Message = "Updating motorcycle with license plate {LicensePlate}.")]
        private static partial void LogUpdating(ILogger logger, string? licensePlate);

        [LoggerMessage(Level = LogLevel.Information, Message = "Deleting motorcycle with license plate {LicensePlate}.")]
        private static partial void LogDeleting(ILogger logger, string? licensePlate);
    }
}
