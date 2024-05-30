#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
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
        public string Model { get => model; set => model = value; }
        public string Prompt { get => prompt; set => prompt = value; }
        public int[] Context { get => context; set => context = value; }
        public bool Stream { get => stream; set => stream = value; }

        [SerializeField] string model = "";
        [SerializeField] string prompt = "";
        [SerializeField] int[] context = Array.Empty<int>();
        [SerializeField] bool stream;

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

    public static async Task<(string description, int[] context)> Generate(HttpClient client, string prompt, int[] context)
    {
        var json = JsonUtility.ToJson(new GenerateRequest
        {
            Model = "phi3",
            Prompt = prompt,
            Context = context,
            Stream = false,
        });
        Debug.Log($"OLLAMA: Sending request '{json}'.");
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "api/generate") { Content = content };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var read = await response.Content.ReadAsStringAsync();
        Debug.Log($"OLLAMA: Received response '{read}'.");
        var result = JsonUtility.FromJson<GenerateResponse>(read);
        return (result.Response.Replace('\n', ' ').Replace('\r', ' '), result.Context ?? Array.Empty<int>());
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
            arrow.Context = icon.Context;
            return true;
        }
        else
            return false;
    }

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