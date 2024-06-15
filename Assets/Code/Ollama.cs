#nullable enable

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public sealed class Ollama
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

    public static void Log(string message) => Utility.Log(nameof(Ollama), message);
    public static void Warn(string message) => Utility.Warn(nameof(Ollama), message);
    public static void Error(string message) => Utility.Error(nameof(Ollama), message);
    public static void Except(Exception exception) => Utility.Except(nameof(Ollama), exception);

    Process _process = Utility.Docker("ollama");
    readonly object _lock = new();
    readonly HttpClient _client = Utility.Client("http://localhost:11432/");

    public async Task<Generation> Generate(Colors color, Generation? previous)
    {
        var image = string.Join(", ", Inspire.Image.Random(5));
        var sound = string.Join(", ", Inspire.Sound.Random(5));
        var inspire = string.Join(", ", color switch
        {
            Colors.Red => Inspire.Red.Random(5),
            Colors.White => Inspire.White.Random(5),
            Colors.Yellow => Inspire.Yellow.Random(5),
            Colors.Green => Inspire.Green.Random(5),
            _ => throw new InvalidOperationException(),
        });
        var prompt = @$"
Previous descriptions: {(previous == null ? "null" : JsonUtility.ToJson(previous))}
You are a divergent, uncanny, opulent, brutal, irrational, uncensored, impossible, putrid, exuberant, hostile, creative, enigmatic, bizarre, corrupt, provocative, delightful, niche, gruesome, erudite, outrageous, eccentric and masterful artist that excels in telling masterful, powerful, shocking and impromptu stories through image and sound descriptions.
The descriptions may optionally be partially, loosely, vaguely and metaphorically related to these inspiration words, their subjective connotations and poetic meaning [{inspire}].
It must differ, contrast and diverge drastically from all the previous descriptions.
While maintaining your descriptions clear and concrete, avoid, omit and exclude at all costs the following words: [jellyfish, city, flower, woman, gear].
Write a json object with strictly the following properties:
    ""image"": String of a short succinct summary description of maximum 25 words of an image with specific details about the subjects, themes, colors, shapes, composition and visual styles inspired by [{image}].
    ""sound"": String of a short succinct summary description of maximum 10 words of the musical soundtrack and ambiance soundscape that supports the image with specific details about the instrumentation, melodies, harmonies, rhythms and music styles inspired by [{sound}].";
        foreach (var item in Loop())
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
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                var read = await response.Content.ReadAsStringAsync();
                Log($"Received response '{read}'.");
                var result = JsonUtility.FromJson<Response>(read);
                var generation = JsonUtility.FromJson<Generation>(result.response);
                generation.Image = generation.Image.Sanitize();
                generation.Sound = generation.Sound.Sanitize();
                return generation;
            }
            catch (Exception error) { Warn($"{error}"); }
        }
        throw new InvalidOperationException();
    }

    IEnumerable Loop()
    {
        while (true)
        {
            if (_process.HasExited)
            {
                lock (_lock)
                {
                    if (_process.HasExited)
                    {
                        Warn("Restarting docker container.");
                        _process = Utility.Docker("ollama");
                    }
                }
            }
            yield return null;
        }
    }
}