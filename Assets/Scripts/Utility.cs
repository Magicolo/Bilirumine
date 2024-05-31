#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

public static class Utility
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

    public static MemoryMappedFile Memory()
    {
        var path = "/dev/shm/bilirumine";
        var memory = MemoryMappedFile.CreateFromFile(path, FileMode.OpenOrCreate, "bilirumine", int.MaxValue, MemoryMappedFileAccess.ReadWrite);
        Application.quitting += () => { try { memory.Dispose(); } catch { } };
        Application.quitting += () => { try { File.Delete(path); } catch { } };
        return memory;
    }

    public static string Cache()
    {
        var path = Path.Join(Application.streamingAssetsPath, "input", ".cache");
        try { Directory.Delete(path, true); } catch { }
        Directory.CreateDirectory(path);
        Application.quitting += () => { try { Directory.Delete(path, true); } catch { } };
        return path;
    }

    public static Process Run(string name, string command, string arguments)
    {
        var process = Process.Start(new ProcessStartInfo(command, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Application.quitting += () => { try { process.Close(); } catch { } };
        Application.quitting += () => { try { process.Kill(); } catch { } };
        Application.quitting += () => { try { process.Dispose(); } catch { } };
        _ = Task.Run(async () =>
        {
            while (!process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;
                Debug.LogWarning($"{name}: {line}");
            }
        });
        return process;
    }

    public static Process Docker(string service)
    {
        var path = Path.Join(Application.streamingAssetsPath, "docker-compose.yml");
        var process = Run(service.ToUpper(), "docker", $"compose --file '{path}' run --service-ports --interactive --rm {service}");
        Application.quitting += () => { try { Process.Start(new ProcessStartInfo("docker", $"compose --file '{path}' kill {service}")); } catch { } };
        return process;
    }

    public static (Process process, HttpClient client) Ollama()
    {
        var process = Docker("ollama");
        var client = new HttpClient() { BaseAddress = new("http://localhost:11432/") };
        return (process, client);
    }

    public static async Task<string> Generate(HttpClient client, string prompt)
    {
        var json = JsonUtility.ToJson(new GenerateRequest
        {
            Model = "phi3",
            Prompt = prompt,
            Stream = false,
            Temperature = 1f,
            Tokens = 50,
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
        return result.Response.Replace('\n', ' ').Replace('\r', ' ');
    }

    public static bool Load(MemoryMappedFile memory, int width, int height, int offset, int size, ref Texture2D? texture)
    {
        if (texture == null || texture.width != width || texture.height != height)
            texture = new Texture2D(width, height, TextureFormat.RGB24, 1, true, true);

        using (var access = memory.CreateViewAccessor())
        {
            unsafe
            {
                var pointer = (byte*)IntPtr.Zero;
                try
                {
                    access.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                    if (pointer == null)
                    {
                        Debug.LogError("Failed to acquire pointer to shared memory.");
                        return false;
                    }
                    var source = (IntPtr)(pointer + offset);
                    texture.LoadRawTextureData(source, size);
                }
                finally { access.SafeMemoryMappedViewHandle.ReleasePointer(); }
            }
        }
        texture.Apply();
        return true;
    }

    public static bool Load(MemoryMappedFile memory, Image output, Main.Frame frame, ref Texture2D? texture)
    {
        if (Load(memory, frame.Width, frame.Height, frame.Offset, frame.Size, ref texture))
        {
            var area = new Rect(0f, 0f, frame.Width, frame.Height);
            var sprite = Sprite.Create(texture, area, area.center);
            output.sprite = sprite;
            return true;
        }
        else
            return false;
    }

    public static bool Load(MemoryMappedFile memory, Arrow arrow, Main.Icon icon)
    {
        if (icon.Tags.HasFlag(arrow.Tags) && Load(memory, icon.Width, icon.Height, icon.Offset, icon.Size, ref arrow.Texture))
        {
            var area = new Rect(0f, 0f, icon.Width, icon.Height);
            arrow.Image.sprite = Sprite.Create(arrow.Texture, area, area.center);
            arrow.Description = icon.Description;
            return true;
        }
        else
            return false;
    }

    public static string Styles(params string[] styles) => Styles(1f, styles);
    public static string Styles(float strength, params string[] styles) => Styles(styles.Select(style => (style, strength)));
    public static string Styles(params (string style, float strength)[] styles) => Styles(styles.AsEnumerable());
    public static string Styles(IEnumerable<(string style, float strength)> styles) => string.Join(" ", styles.Select(pair => pair.strength == 1f ? $"({pair.style})" : $"({pair.style}:{pair.strength})"));

    public static IEnumerable Wait(Task task)
    {
        while (!task.IsCompleted) yield return null;
        if (task is { Exception: { } exception }) throw exception;
    }

    public static IEnumerable Wait(params Task[] tasks) => Wait(Task.WhenAll(tasks));

    public static IEnumerable Wait(ValueTask task)
    {
        while (!task.IsCompleted) yield return null;
    }

    public static IEnumerable Wait<T>(ValueTask<T> task)
    {
        while (!task.IsCompleted) yield return null;
    }

    public static IEnumerable<T?> Wait<T>(IAsyncEnumerable<T> source) where T : class
    {
        var enumerator = source.GetAsyncEnumerator();
        while (true)
        {
            var move = enumerator.MoveNextAsync();
            foreach (var _ in Wait(move)) yield return null;
            if (move.Result) yield return enumerator.Current;
            else yield break;
        }
    }

}