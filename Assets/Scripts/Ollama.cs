#nullable enable

using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class Ollama
{
    [Serializable]
    public sealed record GenerateRequest
    {
        [Serializable]
        sealed record GenerateOptions
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

        public string Model { get => model; set => model = value; }
        public string Prompt { get => prompt; set => prompt = value; }
        public bool Stream { get => stream; set => stream = value; }
        public float Temperature { get => options.temperature; set => options.temperature = value; }
        public int Tokens { get => options.num_predict; set => options.num_predict = value; }
        public int TopK { get => options.top_k; set => options.top_k = value; }
        public float TopP { get => options.top_p; set => options.top_p = value; }

        [SerializeField] string model = "";
        [SerializeField] string prompt = "";
        [SerializeField] bool stream;
        [SerializeField] GenerateOptions options = new();
    }



    [Serializable]
    public sealed record GenerateResponse
    {
        public string Model => model;
        public string CreatedAt => created_at;
        public string Response => response;
        public bool Done => done;
        public string? DoneReason => done_reason;
        public int[]? Context => context;
        public long? TotalDuration => total_duration;
        public long? LoadDuration => load_duration;
        public long? PromptEvalDuration => prompt_eval_duration;
        public int? EvalCount => eval_count;
        public long? EvalDuration => eval_duration;

        [SerializeField] string model = default!;
        [SerializeField] string created_at = default!;
        [SerializeField] string response = default!;
        [SerializeField] bool done;
        [SerializeField] string? done_reason;
        [SerializeField] int[]? context;
        [SerializeField] long? total_duration;
        [SerializeField] long? load_duration;
        [SerializeField] long? prompt_eval_duration;
        [SerializeField] int? eval_count;
        [SerializeField] long? eval_duration;
    }

    public static (Process process, HttpClient client) Create() => (Utility.Docker("ollama"), Utility.Client("http://localhost:11432/"));

    public static async Task<string> Generate(this HttpClient client, string prompt)
    {
        var json = JsonUtility.ToJson(new GenerateRequest
        {
            Model = "phi3",
            Prompt = prompt,
            Stream = false,
            Temperature = 1f,
            Tokens = 100,
            TopK = 100,
            TopP = 0.95f
        });
        Debug.Log($"OLLAMA: Sending request '{json}'.");
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "api/generate") { Content = content };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var read = await response.Content.ReadAsStringAsync();
        Debug.Log($"OLLAMA: Received response '{read}'.");
        var result = JsonUtility.FromJson<GenerateResponse>(read);
        return result.Response.Replace('\n', ' ').Replace('\r', ' ').Replace('(', '<').Replace(')', '>');
    }

}