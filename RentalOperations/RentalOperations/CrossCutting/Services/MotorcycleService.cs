using System.Text.Json;
using Microsoft.Extensions.Options;
using RentalOperations.Configurations;
using RentalOperations.CrossCutting.Model;

namespace RentalOperations.CrossCutting.Services
{
    public class MotorcycleService: IMotorcycleService
    {
        private readonly HttpClient _httpClient;
        private readonly MotoHubSettings _settings;

        public MotorcycleService(IHttpClientFactory httpClientFactory, IOptions<MotoHubSettings> settings)
        {
            _httpClient = httpClientFactory.CreateClient();
            _settings = settings.Value;
            _httpClient.BaseAddress = new Uri(_settings.BaseUrl);
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", _settings.ApiKey);
        }

        public async Task<Motorcycle> GetMotorcycleByIdAsync(string licensePlate)
        {
            try
            {
                var response = await _httpClient.GetAsync($"api/Motorcycles/{licensePlate}");
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"Request failed with status {response.StatusCode}: {errorContent}");
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<Motorcycle>(responseBody);
            }
            catch (HttpRequestException e)
            {
                throw new Exception($"Unable to obtain motorcycle data: {e.Message}", e);
            }
        }
    }
}
