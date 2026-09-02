using System.Net.Http.Json;
using System.Text.Json;

namespace MotoHub.CrossCutting
{
    public class RentalOperationService : IRentalOperationService
    {
        private readonly HttpClient _httpClient;
        public RentalOperationService(IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient("rental-operations");
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

        public async Task<bool> TryRetireMotorcycleAsync(string licensePlate)
        {
            try
            {
                var response = await _httpClient.PostAsync(
                    $"api/Rental/motorcycle-retirements/{Uri.EscapeDataString(licensePlate)}",
                    content: null);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    return false;
                }

                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Request failed with status {response.StatusCode}: {errorContent}");
            }
            catch (HttpRequestException exception)
            {
                throw new Exception(
                    $"Unable to reserve motorcycle retirement: {exception.Message}",
                    exception);
            }
        }

        public async Task<bool> TryReserveMotorcycleRenameAsync(
            string oldLicensePlate,
            string newLicensePlate)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync(
                    "api/Rental/motorcycle-renames/reservations",
                    new
                    {
                        OldLicencePlate = oldLicensePlate,
                        NewLicencePlate = newLicensePlate
                    });
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    return false;
                }

                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Request failed with status {response.StatusCode}: {errorContent}");
            }
            catch (HttpRequestException exception)
            {
                throw new Exception(
                    $"Unable to reserve motorcycle rename: {exception.Message}",
                    exception);
            }
        }
    }
}
