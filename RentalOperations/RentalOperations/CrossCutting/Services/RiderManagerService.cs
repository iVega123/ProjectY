using Microsoft.Extensions.Options;
using RentalOperations.Configurations;
using RentalOperations.CrossCutting.Model;
using System.Text.Json;

namespace RentalOperations.CrossCutting.Services
{
    public class RiderManagerService : IRiderManagerService
    {
        private readonly HttpClient _httpClient;
        private readonly RiderManagerSettings _settings;

        public RiderManagerService(IHttpClientFactory httpClientFactory, IOptions<RiderManagerSettings> settings)
        {
            _httpClient = httpClientFactory.CreateClient();
            _settings = settings.Value;
            _httpClient.BaseAddress = new Uri(_settings.BaseUrl);
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", _settings.ApiKey);
        }

        public async Task<Rider> GetRiderByIdAsync(string riderId)
        {
            try
            {
                var response = await _httpClient.GetAsync($"api/Riders/{riderId}");
                response.EnsureSuccessStatusCode();
                var responseBody = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<Rider>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (HttpRequestException e)
            {
                throw new Exception("Failed to retrieve rider data.", e);
            }
        }
    }
}
