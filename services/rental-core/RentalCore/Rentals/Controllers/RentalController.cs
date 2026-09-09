using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCore.Errors;
using RentalOperations.DTOs;
using RentalOperations.Services;
using System.Security.Claims;

namespace RentalOperations.Controllers
{
    /// <summary>
    /// Sem try/catch.
    ///
    /// Cada endpoint terminava em <c>catch (Exception ex) { return
    /// BadRequest(ex.Message); }</c> -- o achado A9. Aquela linha não sabia a
    /// diferença entre uma frase escrita para o cliente e a mensagem de uma
    /// exceção do Npgsql, que carrega host, banco e usuário; e transformava
    /// toda falha interna em 400, dizendo ao cliente para corrigir uma
    /// requisição que estava certa.
    ///
    /// Quem responde agora é o <see cref="ProblemDetailsExceptionHandler"/>, um
    /// lugar só, em <c>application/problem+json</c> com identificador de
    /// correlação. O que decide o que o cliente vê é o TIPO da exceção -- ver
    /// <see cref="ClientProblemException"/>.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class RentalController : ControllerBase
    {
        private readonly IRentalService _rentalService;

        public RentalController(IRentalService rentalService)
        {
            _rentalService = rentalService;
        }

        private BadRequestObjectResult Invalid(string detail) =>
            BadRequest(ProblemFactory.Create(
                HttpContext, StatusCodes.Status400BadRequest,
                ProblemTypes.InvalidRequest, "Invalid request", detail));

        [HttpPost("create")]
        public async Task<IActionResult> CreateRental([FromBody] RentalCreateDto createDto)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null)
            {
                return Forbid();
            }
            await _rentalService.CreateRentalAsync(createDto, userIdClaim.Value);
            return Ok("Created with Success!");
        }

        [HttpGet("user")]
        public async Task<IActionResult> GetRentalsByUser(
            [FromQuery] string? cursor,
            [FromQuery] int? pageSize)
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

        [Authorize(Roles = "Admin")]
        [HttpGet("user/{userId}")]
        public async Task<IActionResult> GetRentalsByUserAdmin(
            string userId,
            [FromQuery] string? cursor,
            [FromQuery] int? pageSize)
        {
            return Ok(await _rentalService.GetRentalsByUserIdAsync(userId, cursor, pageSize));
        }

        /// <summary>
        /// Fecha o aluguel.
        ///
        /// Chamava-se calculate-final-cost, e o nome era o problema: um POST que
        /// diz "calcular" e na verdade encerra o contrato. Encerrar é o que ele
        /// sempre fez; o cálculo saiu daqui no #137 e a rota passa a dizer isso.
        /// </summary>
        [HttpPost("close")]
        public async Task<IActionResult> Close([FromQuery] string rentalId, [FromQuery] DateTime actualEndDate)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null)
            {
                return Forbid();
            }
            try
            {
                return Ok(await _rentalService.CloseRentalAsync(
                    rentalId, userIdClaim.Value, actualEndDate));
            }
            catch (UnauthorizedAccessException)
            {
                // O único catch que sobra, e ele não formata nada: `Forbid` é
                // uma decisão de autorização, e o pipeline é que sabe como
                // desafiar o chamador.
                return Forbid();
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
                return Invalid("At least one rental id is required.");
            }
            if (requested.Length > RentalService.MaxBatchSize)
            {
                return Invalid(
                    $"A batch may request at most {RentalService.MaxBatchSize} rental ids.");
            }

            var parsed = new List<Guid>(requested.Length);
            foreach (var candidate in requested)
            {
                if (!Guid.TryParse(candidate, out var id))
                {
                    // O identificador recusado NÃO volta na resposta: ele é
                    // entrada do cliente, e devolvê-lo é um refletor pronto.
                    return Invalid("Every rental id must be a UUID.");
                }
                parsed.Add(id);
            }

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

        [HttpGet("is-rented/{motorcycleId:guid}")]
        public async Task<IActionResult> IsMotorcycleRented(Guid motorcycleId)
        {
            return Ok(await _rentalService.IsMotorcycleCurrentlyRentedAsync(motorcycleId));
        }
    }
}
