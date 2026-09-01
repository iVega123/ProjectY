using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using AuthGate.Model;
using AuthGate.DTO;
using System.Security.Claims;
using AuthGate.Validators;
using AuthGate.Services.File;
using AuthGate.Services.RabbitMQ;
using AuthGate.Services;
using AuthGate.Entities;
using AuthGate.Data;
using Microsoft.EntityFrameworkCore;
using ProjectY.Shared.Validation;

namespace AuthGate.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly JwtTokenIssuer _jwtTokenIssuer;
        private readonly ILogger<AuthController> _logger;
        private readonly IFileValidationService _fileValidationService;
        private readonly IMessagingPublisherService _messagingPublisherService;
        private readonly ApplicationDbContext? _dbContext;

        public AuthController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            RoleManager<IdentityRole> roleManager,
            IConfiguration configuration,
            ILogger<AuthController> logger,
            IFileValidationService fileValidationService,
            IMessagingPublisherService messagingPublisherService,
            ApplicationDbContext? dbContext = null
            )
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = roleManager;
            _jwtTokenIssuer = new JwtTokenIssuer(configuration);
            _logger = logger;
            _fileValidationService = fileValidationService;
            _messagingPublisherService = messagingPublisherService;
            _dbContext = dbContext;
        }

        [HttpPost("register/rider")]
        public async Task<IActionResult> RegisterRider([FromForm] RiderRegisterDto model)
        {

            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            if (!await _roleManager.RoleExistsAsync("Rider"))
            {
                _logger.LogInformation("Rider role does not exist; creating new rider role.");
                await _roleManager.CreateAsync(new IdentityRole("Rider"));
            }

            var (isValid, parsedCNHType) = CnhValidator.ParseCNHType(model.CNHType);
            if (!isValid)
            {
                ModelState.AddModelError("TipoCNH", "Invalid CNH Type");
                return BadRequest(ModelState);
            }

            var riderUser = new RiderUser
            {
                UserName = model.Email,
                Name = model.Name,
                Email = model.Email,
                CNPJ = BrazilianCnpj.Normalize(model.CNPJ),
                DateOfBirth = model.DateOfBirth,
                CNHNumber = model.CNHNumber,
                CNHType = parsedCNHType
            };

            (Stream File, string Extension)? validatedImage = null;
            if (model.CNHImage != null)
            {
                validatedImage = await _fileValidationService.ValidateAndConvertFileAsync(model.CNHImage);
            }

            await using var transaction = _dbContext is not null && _dbContext.Database.IsRelational()
                ? await _dbContext.Database.BeginTransactionAsync()
                : null;

            var result = await _userManager.CreateAsync(riderUser, model.Password);
            if (!result.Succeeded)
            {
                _logger.LogError("Failed to create rider user for {Email}. Errors: {Errors}", model.Email, result.Errors);
                return BadRequest(result.Errors);
            }

            _logger.LogInformation("Rider user {UserId} created successfully, assigning 'Rider' role.", riderUser.Id);
            var roleAssignmentResult = await _userManager.AddToRoleAsync(riderUser, "Rider");

            if (!roleAssignmentResult.Succeeded)
            {
                _logger.LogError("Failed to assign 'Rider' role to user {UserId}. Errors: {Errors}", riderUser.Id, roleAssignmentResult.Errors);
                await _userManager.DeleteAsync(riderUser);
                return BadRequest(roleAssignmentResult.Errors);
            }

            _logger.LogInformation("Rider user {UserId} successfully registered.", riderUser.Id);

            _messagingPublisherService.PublishRiderInfo(convertRider(model, riderUser.Id));
            if (validatedImage is not null)
            {
                _messagingPublisherService.PublishImageStream(
                    validatedImage.Value.File,
                    validatedImage.Value.Extension,
                    riderUser.Id);
            }

            if (_dbContext is not null)
            {
                await _dbContext.SaveChangesAsync();
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            return Ok("Rider user successfully registered.");
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto model)
        {
            if (!ModelState.IsValid)
            {
                _logger.LogWarning("Login failed for user {Email}: Invalid model state", model.Email);
                return BadRequest(ModelState);
            }

            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
            {
                _logger.LogWarning("Login failed: no user found with email {Email}", model.Email);
                return Unauthorized();
            }

            var result = await _signInManager.PasswordSignInAsync(user, model.Password, true, lockoutOnFailure: false);
            if (!result.Succeeded)
            {
                _logger.LogWarning("Login failed for user {Email}: {Reason}", model.Email, result.IsLockedOut ? "Account locked out" : "Invalid credentials");
                return Unauthorized();
            }

            _logger.LogInformation("User {Email} successfully authenticated, preparing to generate JWT token.", model.Email);

            var roles = await _userManager.GetRolesAsync(user);
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, user.UserName ?? user.Id),
                new Claim(ClaimTypes.Email, user.Email ?? model.Email),
                new Claim(ClaimTypes.NameIdentifier, user.Id)
            };

            foreach (var role in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }

            string stringToken;
            try
            {
                stringToken = _jwtTokenIssuer.CreateToken(claims, model.Audience);
            }
            catch (ArgumentOutOfRangeException)
            {
                return BadRequest(new { Error = "Unsupported token audience." });
            }

            _logger.LogInformation("JWT token generated for user {Email}.", model.Email);

            return Ok(new { token = stringToken });
        }


        [HttpPost("logout")]
        public async Task<IActionResult> Logout([FromBody] LogoutDto model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Email))
            {
                _logger.LogWarning("Logout attempt failed: No email provided.");
                return BadRequest("Email must be provided for logout.");
            }

            _logger.LogInformation("User {Email} initiating logout", model.Email);
            await _signInManager.SignOutAsync();
            _logger.LogInformation("User {Email} logged out successfully", model.Email);
            return Ok();
        }


        private RiderMQEntity convertRider(RiderRegisterDto model, string id)
        {
            return new RiderMQEntity()
            {
                Name = model.Name,
                UserId = id,
                CNHNumber = model.CNHNumber,
                CNPJ = BrazilianCnpj.Normalize(model.CNPJ),
                CNHType = model.CNHType,
                DateOfBirth = model.DateOfBirth,
                Email = model.Email,
            };
        }
    }
}
