// See https://aka.ms/new-console-template for more information


using System;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using HtmlAgilityPack;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace MyDealzDiscordBot
{
    class Program
    {
        private DiscordSocketClient _client;
        private static readonly HttpClient HttpClient = new HttpClient();

        private ulong GuildId;
        private ulong ChannelId;
        private int ScrapingIntervalMinutes;
        private string Topic;
        private string DiscordToken;

        static CancellationTokenSource cts;
        static Task scrapingTask;

        private List<string> postedDeals = new List<string>();

        static async Task Main(string[] args)
        {
            string ascii = @"
                      _                              _ 
                     | |                            | |
                     | |__   __ ___  ____  __  _ __ | |
                     | '_ \ / _` \ \/ /\ \/ / | '_ \| |
                     | | | | (_| |>  <  >  < _| | | | |
                     |_| |_|\__,_/_/\_\/_/\_(_)_| |_|_|                                                                   
                    ";
            Console.WriteLine("====================================================================================");
            Console.WriteLine("||                           MyDealz Scraper by haxx.nl                           ||");
            Console.WriteLine("====================================================================================\n");
            Console.WriteLine(ascii);
            Console.WriteLine("Type 'start' to start scraping, 'stop' to stop, and 'exit' to close the application, for entering the Settings type 'settings'");

            var program = new Program();
            program.InitializeConfiguration();

            bool running = true;
            while (running)
            {
                var input = Console.ReadLine();
                switch (input.ToLower())
                {
                    case "start":
                        if (scrapingTask == null || scrapingTask.IsCompleted || scrapingTask.IsCanceled)
                        {
                            if (!File.Exists("config.json"))
                                program.UpdateSettings();

                            cts = new CancellationTokenSource();
                            scrapingTask = program.RunBotAsync(cts.Token);
                            Console.WriteLine("Scraping started.");
                        }
                        else
                        {
                            Console.WriteLine("Scraping is already running.");
                        }
                        break;

                    case "stop":
                        if (scrapingTask != null && !scrapingTask.IsCompleted && !scrapingTask.IsCanceled)
                        {
                            cts.Cancel();
                            await scrapingTask;
                            running = false;
                            Console.WriteLine("Scraping stopped.");
                        }
                        else
                        {
                            Console.WriteLine("Scraping is not running.");
                        }
                        break;
                    case "settings":
                        program.UpdateSettings();
                        break;
                    case "exit":
                        if (scrapingTask != null && !scrapingTask.IsCompleted && !scrapingTask.IsCanceled)
                        {
                            cts.Cancel();
                            await scrapingTask;
                        }
                        running = false;
                        break;

                    default:
                        Console.WriteLine("Unknown command. Please type 'start', 'stop', 'settings', or 'exit'.");
                        break;
                }
            }
        }

        private void InitializeConfiguration()
        {
            if (!File.Exists("config.json"))
            {
                Console.WriteLine("config.json file not found. Creating a new file with default values.");
                UpdateSettings();
            }

            try
            {
                var configJson = File.ReadAllText("config.json");

                var config = JObject.Parse(configJson);

                DiscordToken = config.Value<string>("DiscordToken");
                GuildId = config.Value<ulong>("GuildId");
                ChannelId = config.Value<ulong>("ChannelId");
                ScrapingIntervalMinutes = config.Value<int>("ScrapingIntervalMinutes");
                Topic = config.Value<string>("Topic");
            }
            catch
            {
                UpdateSettings();
                var configJson = File.ReadAllText("config.json");

                var config = JObject.Parse(configJson);

                DiscordToken = config.Value<string>("DiscordToken");
                GuildId = config.Value<ulong>("GuildId");
                ChannelId = config.Value<ulong>("ChannelId");
                ScrapingIntervalMinutes = config.Value<int>("ScrapingIntervalMinutes");
                Topic = config.Value<string>("Topic");
            }
        }

        public async Task RunBotAsync(CancellationToken cancellationToken)
        {
            _client = new DiscordSocketClient();
            _client.Log += Log;

            // Initialize the semaphore
            SemaphoreSlim readySemaphore = new SemaphoreSlim(0, 1);

            // Wait for the Ready event
            _client.Ready += async () =>
            {
                // Release the semaphore
                readySemaphore.Release();
            };

            await _client.LoginAsync(TokenType.Bot, DiscordToken);
            await _client.StartAsync();

            // Wait for the Ready event before starting the scraping loop
            await readySemaphore.WaitAsync(cancellationToken);

            // Start the scraping loop
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ScrapeMyDealzAsync();
                    await Task.Delay(TimeSpan.FromMinutes(ScrapingIntervalMinutes), cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    // Do any necessary cleanup here
                    return;
                }
            }
        }

        private Task Log(LogMessage msg)
        {
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        }
        private async Task ScrapeMyDealzAsync()
        {
            var hotDealsUrl = "https://www.mydealz.de/hot?page=1";
            var newDealsUrl = "https://www.mydealz.de/new?page=1";

            await ScrapeAsync(hotDealsUrl, "Hot Deals");
            await ScrapeAsync(newDealsUrl, "New Deals");
        }

        private async Task ScrapeAsync(string url, string type)
        {
            var html = await GetHtmlAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var threadGridElements = doc.DocumentNode.Descendants("div")
                .Where(d => d.GetAttributeValue("class", "").Contains("threadGrid-title"));

            if (!threadGridElements.Any())
            {
                Console.WriteLine("Keine übereinstimmenden Elemente gefunden. Überprüfen Sie die Klassenauswahl.");
                return;
            }

            var threads = threadGridElements
                .SelectMany(d => d.Descendants("a"))
                .Select(node => (title: node.InnerText.Trim(), link: node.GetAttributeValue("href", null)))
                .Where(tuple => !string.IsNullOrEmpty(tuple.link) && Contains(tuple.title))
                .ToList();

            var guild = _client.GetGuild(GuildId);
            if (guild == null)
            {
                Console.WriteLine("Die angegebene Gilden-ID wurde nicht gefunden. Bitte überprüfen Sie die Gilden-ID.");
                return;
            }

            var channel = guild.GetTextChannel(ChannelId);
            if (channel == null)
            {
                Console.WriteLine("Der angegebene Kanal wurde nicht gefunden. Bitte überprüfen Sie die Kanal-ID.");
                return;
            }

            foreach (var (title, link) in threads)
            {
                if (!postedDeals.Contains(link))
                {
                    var dealMessage = $"[{type}] {title}: {await ShortenUrlAsync(link)}";
                    await channel.SendMessageAsync(dealMessage);

                    // Schritt 3: Fügen Sie den neuen Deal der Liste postedDeals hinzu
                    postedDeals.Add(link);
                }
            }
        }

        private void SaveConfiguration()
        {
            var jsonData = JsonConvert.SerializeObject(new
            {
                DiscordToken = DiscordToken,
                GuildId = GuildId,
                ChannelId = ChannelId,
                ScrapingIntervalMinutes = ScrapingIntervalMinutes,
                Topic = Topic
            }, Formatting.Indented) ;

            File.WriteAllText("config.json", jsonData);
        }

        private void UpdateSettings()
        {
            Console.WriteLine("Updating settings...");

            Console.Write($"Enter Guild ID (current: {GuildId}): ");
            if (ulong.TryParse(Console.ReadLine(), out ulong newGuildId)) GuildId = newGuildId;

            Console.Write($"Enter Channel ID (current: {ChannelId}): ");
            if (ulong.TryParse(Console.ReadLine(), out ulong newChannelId)) ChannelId = newChannelId;

            Console.Write($"Enter Scraping Interval in minutes (current: {ScrapingIntervalMinutes}): ");
            if (int.TryParse(Console.ReadLine(), out int newScrapingInterval)) ScrapingIntervalMinutes = newScrapingInterval;

            if (string.IsNullOrEmpty(Topic)) Topic = "All";
            Console.Write($"A specific topic (current: {Topic}) :");
            if (int.TryParse(Console.ReadLine(), out int newTopic)) ScrapingIntervalMinutes = newTopic;

            SaveConfiguration();

            Console.WriteLine("Settings updated and saved.");
        }


        private async Task<string> GetHtmlAsync(string url)
        {
            using var response = await HttpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private bool Contains(string text)
        {
            if (string.IsNullOrEmpty(Topic) || Topic == "All")
            {
                var regex = new Regex(@"\b\b", RegexOptions.IgnoreCase);
                return regex.IsMatch(text);
            }
            else
            {
                var regex = new Regex($@"\b{Topic}\b", RegexOptions.IgnoreCase);
                return regex.IsMatch(text);
            }

        }

        private static async Task<string> ShortenUrlAsync(string url)
        {
            try
            {
                var response = await HttpClient.GetAsync($"https://is.gd/create.php?format=simple&url={Uri.EscapeDataString(url)}");
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch
            {
                Console.WriteLine("Shortener-Service nicht erreichbar. Verwende vollen Link.");
                return url;
            }
        }
    }
}
