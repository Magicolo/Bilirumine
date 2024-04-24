#nullable enable

using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public sealed class Comfy : MonoBehaviour
{
    const int Port = 8199;
    static readonly string _path = Path.Join(Application.streamingAssetsPath, "Comfy");

    public bool Do;
    public string? Prompt;
    public uint Width = 1152;
    public uint Height = 896;
    public uint Steps = 10;
    public float Guidance = 2.5f;
    public Renderer? Renderer;
    [Header("Debug")]
    public Texture2D? Texture;
    public string? Container;


    void Start()
    {
        Texture = new(2, 2);
        StartCoroutine(Serve());
    }

    void Update()
    {
        if (Do) { Do = false; StartCoroutine(Generate()); }
    }

    void OnDestroy()
    {
        if (string.IsNullOrWhiteSpace(Container)) return;
        else _ = Run("docker", $"stop {Container}");
    }

    IEnumerator Serve()
    {
        var task = Run(Path.Join(_path, "serve.sh"), $"{Port}");
        while (!task.IsCompleted) yield return null;

        var container = task.Result?.Trim();
        if (string.IsNullOrWhiteSpace(container))
            UnityEngine.Debug.LogError($"Comfy failed to start container.");
        else
        {
            UnityEngine.Debug.Log($"Comfy container serving in '{container}'.");
            Container = container;
        }
    }

    IEnumerator Generate()
    {
        if (string.IsNullOrWhiteSpace(Container))
        {
            UnityEngine.Debug.LogError("Missing comfy container identifier.");
            yield break;
        }

        var generateTask = Run(Path.Join(_path, "generate.sh"), $"--container {Container} {Option("prompt", Prompt)} --width {Width} --height {Height} --steps {Steps} --guidance {Guidance}");
        while (!generateTask.IsCompleted) yield return null;

        var path = generateTask.Result?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            UnityEngine.Debug.LogError($"Comfy failed to generate image.");
        else
            UnityEngine.Debug.Log($"Comfy generated image at '{path}'.");

        if (Renderer is not null)
        {
            var imageTask = File.ReadAllBytesAsync(path);
            while (!imageTask.IsCompleted) yield return null;

            var image = imageTask.Result;
            Texture.LoadImage(image);
            Renderer.transform.localScale = new(Width, Height, 1);
            Renderer.material.mainTexture = Texture;
        }
    }

    static string Option(string key, string? value) => string.IsNullOrWhiteSpace(value) ? "" : $"--{key} '{value}'";

    static int _counter;
    static async Task<string?> Run(string command, string arguments = "")
    {
        var count = Interlocked.Increment(ref _counter);
        UnityEngine.Debug.Log($"Command({count}) start: '{command} {arguments}'");
        using var process = Process.Start(new ProcessStartInfo(command, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await Task.Run(process.WaitForExit);
        if (string.IsNullOrWhiteSpace(error))
        {
            UnityEngine.Debug.Log($"Command({count}) success with '{output}'.");
            return output;
        }
        else
        {
            UnityEngine.Debug.LogError($"Command({count}) failure with '{error}'.");
            return null;
        }
    }
}
