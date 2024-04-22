using Microsoft.Extensions.Options;
using MotoHub.Configurations;
using System.Text.Json;

namespace MotoHub.CrossCutting
{
    public class RentalOperationService : IRentalOperationService
    {
        private readonly HttpClient _httpClient;
        private readonly RentalOperationsSettings _settings;

        public RentalOperationService(IHttpClientFactory httpClientFactory, IOptions<RentalOperationsSettings> settings)
        {
            _httpClient = httpClientFactory.CreateClient();
            _settings = settings.Value;
            _httpClient.BaseAddress = new Uri(_settings.BaseUrl);
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", _settings.ApiKey);
        }

        public async Task<bool> GetRentalsByMotorcycleLicencePlateAsync(string licensePlate)
        {
            try
            {
                var response = await _httpClient.GetAsync($"api/Rental/is-rented/{licensePlate}");
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"Request failed with status {response.StatusCode}: {errorContent}");
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<bool>(responseBody);
            }
            catch (HttpRequestException e)
            {
                throw new Exception($"Unable to obtain motorcycle data: {e.Message}", e);
            }
        }
    }
}
