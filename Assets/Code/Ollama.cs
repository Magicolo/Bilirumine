#nullable enable

using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class Ollama
{
    [Serializable]
    sealed record Options
    {
        /// The temperature of the model. Increasing the temperature will make the model answer more creatively. (Default: 0.8) 	float 	temperature 
        public float temperature;
        /// Maximum number of tokens to predict when generating text. (Default: 128, -1 = infinite generation, -2 = fill context) 	int 	
        public int num_predict;
        /// Reduces the probability of generating nonsense. A higher value (e.g. 100) will give more diverse answers, while a lower value (e.g. 10) will be more conservative. (Default: 40) 	int 	
        public int top_k;
        /// Works together with top-k. A higher value (e.g., 0.95) will lead to more diverse text, while a lower value (e.g., 0.5) will generate more focused and conservative text. (Default: 0.9) 	float 	top_p 
        public float top_p;
    }

    [Serializable]
    sealed record Request
    {
        public string model = "";
        public string prompt = "";
        public string format = "";
        public bool stream;
        public Options options = new();
    }

    [Serializable]
    sealed record Response
    {
        public string model = default!;
        public string created_at = default!;
        public string response = default!;
        public bool done;
        public string? done_reason;
        public int[]? context;
        public long? total_duration;
        public long? load_duration;
        public long? prompt_eval_duration;
        public int? eval_count;
        public long? eval_duration;
    }

    [Serializable]
    public record Generation
    {
        public string Image { get => image; set => image = value; }
        public string Sound { get => sound; set => sound = value; }

        [SerializeField] string image = default!;
        [SerializeField] string sound = default!;
    }

    public static (Process process, HttpClient client) Create() => (Utility.Docker("ollama"), Utility.Client("http://localhost:11432/"));

    public static async Task<Generation> Generate(this HttpClient client, Colors color, Generation[] previous)
    {
        var image = string.Join(", ", Inspire.Image.Random(4));
        var sound = string.Join(", ", Inspire.Sound.Random(4));
        var inspire = string.Join(", ", color switch
        {
            Colors.Red => Inspire.Red.Random(4),
            Colors.White => Inspire.White.Random(4),
            Colors.Yellow => Inspire.Yellow.Random(4),
            Colors.Green => Inspire.Green.Random(4),
            _ => throw new InvalidOperationException(),
        });
        var prompt = @$"
Previous descriptions: [{string.Join(", ", previous.Select(JsonUtility.ToJson))}]
You are a divergent, uncensored, creative, provocative and eccentric artist that excels in telling masterful, powerful and impromptu stories through image and sound descriptions.
The descriptions may optionally be loosely vaguely metaphorically related to these inspiration words, their connotations and poetic meaning: '{inspire}'.
It must follow narratively from the previous descriptions.
Write a json object with strictly the following properties:
    ""image"": string of a short succinct summary description of maximum 25 words of an image with specific details about the subjects, themes, colors, shapes, composition and visual styles inspired by '{image}'
    ""sound"": string of a short succinct summary description of maximum 10 words of the musical soundtrack and ambiance soundscape that supports the image with specific details about the instrumentation, melodies, harmonies, rhythms and music styles inspired by '{sound}'";
        while (true)
        {
            try
            {
                var json = JsonUtility.ToJson(new Request
                {
                    model = "llama3",
                    prompt = prompt,
                    stream = false,
                    format = "json",
                    options = new() { temperature = 1f, num_predict = 1000, top_k = 100, top_p = 0.95f }
                });
                Log($"Sending request '{json}'.");
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var request = new HttpRequestMessage(HttpMethod.Post, "api/generate") { Content = content };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                var read = await response.Content.ReadAsStringAsync();
                Log($"Received response '{read}'.");
                var result = JsonUtility.FromJson<Response>(read);
                var generation = JsonUtility.FromJson<Generation>(result.response);
                return new()
                {
                    Image = generation.Image.Sanitize(),
                    Sound = generation.Sound.Sanitize(),
                };
            }
            catch (Exception error) { Warn($"{error}"); }
        }
    }

    public static void Log(string message) => Debug.Log($"OLLAMA: {message}");
    public static void Warn(string message) => Debug.LogWarning($"OLLAMA: {message}");
    public static void Error(string message) => Debug.LogError($"OLLAMA: {message}");
}