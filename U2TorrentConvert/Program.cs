using BencodeNET.Parsing;
using BencodeNET.Torrents;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace U2TorrentConvert
{
    public class ResponseItemModel
    {
        public string jsonrpc { get; set; }
        public string result { get; set; }
        public int id { get; set; }
        public ResponseItemErrorModel error { get; set; }

        public class ResponseItemErrorModel
        {
            public int code { get; set; }
            public string message { get; set; }
        }
    }
    public class RequestItemModel
    {
        public string jsonrpc { get; set; }
        public string method { get; set; }
        public string[] @params { get; set; }
        public int id { get; set; }
    }

    public class Program
    {
        private static BencodeParser parser = new BencodeParser();

        public static async Task Main(string[] args)
        {
            Dictionary<string, string> files = new Dictionary<string, string>();
            string originFolder = Path.Combine(Environment.CurrentDirectory, "Origin");

            if (!Directory.Exists(originFolder))
            {
                Console.Error.WriteLine($"The original torrent files should be placed into {originFolder}");
                return;
            }

            if (!File.Exists("apikey.txt"))
            {
                Console.Error.WriteLine($"The API key should be written into apikey.txt");
                return;
            }

            string requestApiUrl = File.ReadAllText("apikey.txt");

            if (requestApiUrl == "PASTE YOUR URL HERE!")
            {
                Console.Error.WriteLine($"You should paste your API Key (obtained from https://u2.dmhy.org/privatetorrents.php) into apikey.txt");
                return;
            }

            foreach (var originalFilePath in Directory.EnumerateFiles(originFolder, "*.torrent"))
            {
                FileStream fs = File.Open(originalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                Torrent torrent = parser.Parse<Torrent>(fs);
                fs.Close();

                if (torrent.Trackers.Any(x => x.Any(y => y.Contains("dmhy.org"))))
                {
                    string infoHash = torrent.OriginalInfoHash;
                    Console.WriteLine($"{Path.GetFileName(originalFilePath)} {infoHash}");
                    files.Add(infoHash, originalFilePath);
                }
            }

            HttpClient client = new HttpClient();

            // Process the torrent files in batch of 50 files.
            for (int i = 0; i < files.Count / 50 + 1; i++)
            {
                var requestItems = files.Skip(i * 50).Take(50).Select((x, i) => new RequestItemModel
                {
                    jsonrpc = "2.0",
                    method = "query",
                    @params = new string[] { x.Key },
                    id = i + 1
                }).ToArray();

                Console.ForegroundColor = ConsoleColor.Green;
                string requestItemsAsBody = JsonSerializer.Serialize(requestItems);
                Console.WriteLine(requestItemsAsBody);
                Console.ResetColor();

                int retry = 0;
                while (retry < 10 && requestItems.Length > 0)
                {
                    var content = new StringContent(requestItemsAsBody, Encoding.UTF8, "application/json");
                    var request = client.PostAsync(requestApiUrl, content);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"=== BATCH {i}, ATTEMPT {retry} ===");
                    Console.ResetColor();

                    HttpResponseMessage response = await request;

                    // For 503 Service Unavailable, retry after the specified seconds.
                    if (response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Headers.Contains("Retry-After"))
                    {
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        Console.Error.WriteLine($"Retry after {Convert.ToInt32(response.Headers.RetryAfter.Delta.Value.TotalMilliseconds)}ms");
                        Console.ResetColor();
                        Thread.Sleep(Convert.ToInt32(response.Headers.RetryAfter.Delta.Value.TotalMilliseconds));
                    }

                    // For 403 Foribbden, the API key is wrong.
                    else if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        Console.Error.WriteLine("Invalid API KEY");
                        Console.WriteLine(await response.Content.ReadAsStringAsync());
                        Console.ResetColor();
                        return;
                    }

                    else if (response.StatusCode == HttpStatusCode.OK)
                    {
                        string responseContent = await response.Content.ReadAsStringAsync();

                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine(responseContent);
                        Console.ResetColor();

                        ResponseItemModel[] responseItems = JsonSerializer.Deserialize<ResponseItemModel[]>(responseContent);
                        await UpdateFilesAsync(files, requestItems, responseItems);

                        Thread.Sleep(3000);
                        break;
                    }

                    else
                    {
                        // For 500 Internal Server Error, or other error, just retry after 5000ms, with up to 10 attempts.
                        retry++;
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        Console.Error.WriteLine(await response.Content.ReadAsStringAsync());
                        Console.ResetColor();

                        Thread.Sleep(3000);
                    }
                }
            }

            Console.WriteLine("All files have been processed. Please review log above for anything wrong.");
            Console.WriteLine("Press Enter to exit.");
            Console.ReadLine();
        }

        private static async Task UpdateFilesAsync(Dictionary<string, string> files, RequestItemModel[] requestItems, ResponseItemModel[] responseItems)
        {
            string newFolder = Path.Combine(Environment.CurrentDirectory, "New");
            if (!Directory.Exists(newFolder))
            {
                Directory.CreateDirectory(newFolder);
            }

            foreach (var item in responseItems)
            {
                string infoHash = requestItems.FirstOrDefault(x => x.id == item.id).@params[0];
                if (infoHash == null)
                {
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.Error.WriteLine($"File of Infohash {infoHash} is not found, which is unexpected.");
                    Console.ResetColor();
                    continue;
                }

                string originalFilePath = files[infoHash];

                if (item.error == null)
                {
                    string newFilePath = Path.Combine(newFolder, Path.GetFileName(originalFilePath));

                    FileStream fs = File.Open(originalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    Torrent torrent = parser.Parse<Torrent>(fs);
                    fs.Close();

                    await UpdateFileTrackerAsync(torrent, newFilePath, item.result);
                }
                else
                {
                    Console.WriteLine($"{Path.GetFileName(originalFilePath)}: {item.error.code} {item.error.message}");
                }
            }
        }

        private static async Task UpdateFileTrackerAsync(Torrent torrent, string newFilePath, string newSecureKey)
        {
            for (int i = 0; i < torrent.Trackers.Count; i++)
            {
                for (int j = 0; j < torrent.Trackers[i].Count; j++)
                {
                    if (torrent.Trackers[i][j].Contains("dmhy.org"))
                    {
                        torrent.Trackers[i][j] = $"https://daydream.dmhy.best/announce?secure={newSecureKey}";
                    }
                }
            }

            using (var stream = File.OpenWrite(newFilePath))
            {
                await torrent.EncodeToAsync(stream);
            }
        }
    }
}
