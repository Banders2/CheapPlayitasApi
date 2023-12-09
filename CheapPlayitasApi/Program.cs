using Microsoft.Extensions.Caching.Memory;
using System.Net;

namespace CheapPlayitasApi
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            ServicePointManager.DefaultConnectionLimit = 30;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            builder.Services.AddHttpClient();
            builder.Services.AddMemoryCache();

            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(builder =>
                {
                    builder.AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader();
                });
            });

            var app = builder.Build();
            app.UseSwagger();
            app.UseSwaggerUI();
            var httpClient = app.Services.GetRequiredService<HttpClient>();
            var cache = app.Services.GetRequiredService<IMemoryCache>();

            app.MapGet("/api/prices", async (HttpContext context) =>
            {
                Console.WriteLine($"Starting request to prices {DateTime.Now}");

                var persons = context.Request.Query.TryGetValue("persons", out var personsValue) ? int.Parse(personsValue) : 2;
                var cacheKey = $"PricesCache{persons}";
                var prices = cache.GetOrCreate(cacheKey, async entry =>
                {
                    try
                    {
                        entry.SetAbsoluteExpiration(TimeSpan.FromHours(20));
                        var travelPrices = await GetHotelsAndPrices(httpClient, persons);
                        return travelPrices;
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Failed to get cache - {e.Message}");
                        cache.Remove(cacheKey);
                        throw;
                    }

                });
                var res = await prices;
                if(res.Count == 0) cache.Remove(cacheKey);
                Console.WriteLine($"Finishing request to prices {DateTime.Now}");
                return res;
            });

            app.UseCors();

            await app.RunAsync();
        }

        public static async Task<List<TravelPrice>> GetHotelsAndPrices(HttpClient httpClient, int persons)
        {
            //var airports = new List<string>() { "BLL" };
            var airports = new List<string>() { "CPH", "BLL", "AAL" };
            //var durations = new List<string>() { "21" };
            var durations = new List<string>() { "7", "14", "21" };
            //var hotels = GetHotelList().SkipLast(7).ToList();
            var hotels = GetHotelList();
            var prices = await GetPricesAsync(httpClient, durations, hotels, airports, persons);

            return prices;
        }

        public static async Task<List<TravelPrice>> GetPricesAsync(HttpClient httpClient, List<string> durations, List<Hotel> hotels, List<string> airports, int persons)
        {
            var travelPrices = new List<TravelPrice>();
            var paxAges = string.Join(",", Enumerable.Repeat("18", persons));
            var monthYearStrings = GetMonthYearStrings();

            var tasks = new List<Task<List<TravelPrice>>>();
            durations.ForEach(duration => airports.ForEach(airport => monthYearStrings.ForEach(monthYearString => tasks.Add(GetPricesForHotelAsync(httpClient, duration, hotels, airport, monthYearString, paxAges)))));
            var results = await Task.WhenAll(tasks);

            foreach (var result in results)
            {
                travelPrices.AddRange(result);
            }

            return travelPrices;
        }

        public static async Task<List<TravelPrice>> GetPricesForHotelAsync(HttpClient httpClient, string travelDuration, List<Hotel> hotels, string airport, string monthYearString, string paxAges)
        {
            var tasks = hotels.Select(hotel => GetPricesForHotelAndAirportAsync(httpClient, travelDuration, hotel, airport, monthYearString, paxAges));
            var results = await Task.WhenAll(tasks);
            return results.SelectMany(x => x).ToList();
        }

        public static async Task<List<TravelPrice>> GetPricesForHotelAndAirportAsync(HttpClient httpClient, string travelDuration, Hotel hotel, string airport, string monthYearString, string paxAges)
        {
            var url = $"https://www.apollorejser.dk/PriceCalendar/Calendar?ProductCategoryCode=FlightAndHotel&DepartureDate={monthYearString}-01&departureAirportCode={airport}&duration={travelDuration}&catalogueItemId={hotel.HotelId}&departureDateRange=31&paxAges={paxAges}";

            var response = await httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var data = await response.Content.ReadFromJsonAsync<List<TravelPriceDto>>();
                if (data != null && data.Count > 0)
                {
                    var travels = data
                        .Where(d => !d.IsSoldOut && d.CheapestPrice != null)
                        .Select(travelPriceDto => new TravelPrice(
                            travelPriceDto.Date,
                            travelPriceDto.CheapestPrice,
                            airport,
                            travelDuration,
                            hotel.DisplayName,
                            HotelLink(hotel.HotelUrl, travelPriceDto.Date, airport, travelDuration, hotel.HotelId, paxAges)))
                        .ToList();
                    if(travelDuration != "21") { return travels; }

                    // Figure out of travels are 21 or 28 days. This should be optimized!
                    var travelsAt21Days = travels.Where(x => x.Duration == "21").ToList();
                    travels.Clear(); // Remove existing travels, since they are either 21, 28 or both.
                    foreach (var travel in travelsAt21Days)
                    {
                        var flightsUrl = $"https://www.apollorejser.dk/api/Flight/Flights?productCategoryCode=FlightAndHotel&DepartureAirportCode={airport}&DepartureDate={travel.Date:yyyy-MM-dd}&duration=21&HotelId={hotel.HotelIdString}&PaxAges={paxAges}";
                        var flightsResponse = await httpClient.GetAsync(flightsUrl);
                        var flightsData = await flightsResponse.Content.ReadFromJsonAsync<FLights>();
                        if (flightsData != null && flightsData.FlightPackages.Count > 0)
                        {
                            foreach (var flight in flightsData.FlightPackages)
                            {
                                travels.Add(
                                    new TravelPrice(
                                        travel.Date,
                                        flight.CheapestProductPrice,
                                        airport,
                                        flight.DurationInDays.ToString(),
                                        hotel.DisplayName,
                                        HotelLink(hotel.HotelUrl, travel.Date, airport, travelDuration, hotel.HotelId, paxAges)
                                    )
                                );
                            }
                        }
                    }
                    return travels;
                }
            }
            return new List<TravelPrice>();
        }

        private static List<string> GetMonthYearStrings()
        {
            var monthYearStrings = new List<string>();
            var date = DateTime.Now.Date;
            var currentMonth = date.Month;
            var currentYear = date.Year;
            const int endMonth = 12;

            var years = new List<int> { currentYear, currentYear + 1 };

            foreach (var year in years)
            {
                var startMonth = year == currentYear ? currentMonth : 1;
                for (var month = startMonth; month <= endMonth; month++)
                {
                    var monthStr = month.ToString("00");
                    var monthYearString = $"{year}-{monthStr}";
                    monthYearStrings.Add(monthYearString);
                }
            }
            return monthYearStrings;
        }

        private static string HotelLink(string hotelUrl, DateTime date, string airport, string duration, string hotelId, string paxAges)
        {
            return $"https://www.apollorejser.dk/{hotelUrl}?departureDate={date.ToString("yyyy-MM-dd")}&departureAirportCode={airport}&duration={duration}&catalogueItemId={hotelId}&departureDateRange=31&paxAges={paxAges}";
        }

        public static List<Hotel> GetHotelList()
        {
            var hotels = new List<Hotel>
            {
                new Hotel("Playitas Annexe (Fuerteventura - Spanien)", "530116", "PLXPLA", "spanien/de-kanariske-oer/fuerteventura/playitas-resort/hoteller/playitas-annexe"),
                new Hotel("Playitas Resort (Fuerteventura - Spanien)", "160759", "PLYBAH", "spanien/de-kanariske-oer/fuerteventura/playitas-resort/hoteller/playitas-resort"),
                //new Hotel("La Pared (Fuerteventura - Spanien)", "537065", "COSLAP", "spanien/de-kanariske-oer/fuerteventura/costa-calma-tarajalejo-og-la-pared/hoteller/la-pared---powered-by-playitas"),
                //new Hotel("Porto Myrina (Limnos - Grækenland)", "158862", "MYNPPB", "graekenland/limnos/hoteller/porto-myrina---powered-by-playitas"),
                //new Hotel("Levante (Rhodos - Grækenland)", "165291", "AFNLEV", "graekenland/rhodos/afandou-og-kolymbia/hoteller/levante---powered-by-playitas"),
                //new Hotel("Sivota Retreat (Grækenland)", "544616", "SIVNEI", "graekenland/sivota/hoteller/sivota-retreat---powered-by-playitas"),
                //new Hotel("Cavo Spada Deluxe & Spa Giannoulis (Kreta - Grækenland)", "542262", "KLYGCS", "graekenland/kreta/kolymbari/hoteller/cavo-spada-deluxe-og-spa-giannoulis-hotels"),
                //new Hotel("Aqua Vista (Egypten)", "548420", "HURAQV", "egypten/hurghada/hoteller/aqua-vista---powered-by-playitas"),
                //new Hotel("Vidamar Resorts (Madeira - Portugal)", "1204396", "MAEVID", "portugal/madeira/funchal/hoteller/vidamar-resorts-madeira---vinter")
            };

            return hotels;
        }
    }
    public record TravelPriceDto(DateTime Date, bool IsSoldOut, decimal? CheapestPrice);

    public record Hotel(string DisplayName, string HotelId, string HotelIdString, string HotelUrl);

    public record TravelPrice(DateTime Date, decimal? Price, string Airport, string Duration, string Hotel, string Link);


    public record FlightPackage
    {
        public int DurationInDays { get; init; }
        public decimal CheapestProductPrice { get; init; }
    }

    public record FLights
    {
        public List<FlightPackage> FlightPackages { get; init; }
    }
}
