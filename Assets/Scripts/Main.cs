#nullable enable

using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Linq;
using System;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Threading;
using System.IO;
using UnityEngine.Experimental.Rendering;
using Random = UnityEngine.Random;
using TMPro;
using System.Collections.Generic;
using UnityEngine.Rendering;
using Unity.Collections;

public sealed class Main : MonoBehaviour
{
    public enum ComfyGeneration
    {
        None,
        Once,
        Infinite
    }

    [Serializable]
    public sealed class ComfySettings
    {
        public ComfyGeneration Generate;
        public string? Prompt = "Cuppa";
        public uint Width = 800;
        public uint Height = 600;
        public uint Steps = 7;
        public float Guidance = 2f;

        [Header("Debug")]
        public Texture2D? Texture;
        public string? Container;
        public string Path = System.IO.Path.Join(Application.streamingAssetsPath, "Comfy");
    }

    public enum Modes
    {
        Output,
        Camera,
        Comfy
    }

    [Serializable]
    public sealed class CameraSettings
    {
        public int X = 128;
        public int Y = 128;
        public int Rate = 30;
        public int Device = 0;
        public Camera Camera = default!;
    }

    static readonly Modes[] _modes = (Modes[])typeof(Modes).GetEnumValues();

    static string Option(string key, string? value) => string.IsNullOrWhiteSpace(value) ? "" : $"--{key} '{value}'";

    static RenderTexture Render(Vector2Int size) => new(size.x, size.y, 1, GraphicsFormat.R32G32B32A32_SFloat)
    {
        enableRandomWrite = true,
        filterMode = FilterMode.Point
    };

    static Texture2D Texture(Vector2Int size) => new(size.x, size.y, GraphicsFormat.R32G32B32A32_SFloat, TextureCreationFlags.None)
    {
        filterMode = FilterMode.Point,
    };

    static NativeArray<Color> Buffer(int count)
    {
        var buffer = new NativeArray<Color>(count, Allocator.Persistent);
        Application.quitting += () => { AsyncGPUReadback.WaitAllRequests(); buffer.Dispose(); };
        return buffer;
    }

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

    public ComfySettings Comfy = new();
    public CameraSettings Camera = new();
    [Range(0f, 1f)]
    public float Delta = 0.01f;
    public ComputeShader Shader = default!;
    public Image Output = default!;
    public TMP_Text Text = default!;

    (bool a, bool b, bool c, bool d, bool tab, bool shift, bool space, bool left, bool right, bool up, bool down) _buttons;

    IEnumerator Start()
    {
        Comfy.Texture = Texture(new(2, 2));
        StartCoroutine(Serve().GetEnumerator());

        UnityEngine.Debug.Log($"Camera devices: {string.Join(", ", WebCamTexture.devices.Select(device => device.name))}");
        if (!WebCamTexture.devices.TryAt(Camera.Device, out var device))
        {
            UnityEngine.Debug.LogWarning("Camera not found.");
            yield break;
        }

        var texture = new WebCamTexture(device.name, Camera.X, Camera.Y, Camera.Rate)
        {
            autoFocusPoint = null,
            filterMode = FilterMode.Point,
        };
        texture.Play();
        while (texture.width < 32 && texture.height < 32) yield return null;
        UnityEngine.Debug.Log($"Camera: {texture.deviceName} | Resolution: {texture.width}x{texture.height} | FPS: {texture.requestedFPS} | Graphics: {texture.graphicsFormat}");

        var deltas = new Queue<float>();
        var mode = Modes.Output;
        var size = new Vector2Int(texture.width, texture.height);
        var camera = (input: texture, output: Texture(size), buffer: Buffer(size.x * size.y));
        var output = Render(size);
        var time = Time.time;
        var request = AsyncGPUReadback.RequestIntoNativeArray(ref camera.buffer, camera.input);

        while (true)
        {
            yield return null;
            Cursor.visible = Application.isEditor;

            var delta = Time.time - time;
            while (delta > 0 && deltas.Count >= 100) deltas.Dequeue();
            while (delta > 0 && deltas.Count < 100) deltas.Enqueue(1f / delta);
            if (_buttons.tab) mode = _modes[((int)mode + 1) % _modes.Length];
            Text.text = _buttons.shift || mode > 0 ?
$@"FPS: {deltas.Average():0.00}
Mode: {mode}
Resolution: {size.x} x {size.y}" : "";

            Shader.SetInt("Width", size.x);
            Shader.SetInt("Height", size.y);
            Shader.SetTexture(0, "Output", output);
            Shader.SetTexture(0, "CameraInput", camera.input);
            Shader.SetFloat("Delta", Delta);
            Shader.SetBool("Clear", _buttons.space);

            var steps = (int)(delta / Delta);
            for (int step = 0; step < Math.Clamp(steps, 1, 10); step++)
            {
                Shader.SetFloat("Time", time + step * Delta);
                Shader.SetVector("Seed", new Vector4(Random.value, Random.value, Random.value, Random.value));
                Shader.Dispatch(0, size.x / 8, size.y / 4, 1);
            }
            time += steps * Delta;

            Output.material.mainTexture = mode switch
            {
                Modes.Output => output,
                Modes.Camera => camera.output,
                Modes.Comfy => Comfy.Texture,
                _ => default
            };
            Output.SetMaterialDirty();
            camera.output.LoadRawTextureData(camera.input.GetNativeTexturePtr(), camera.input.width * camera.input.height);

            // if (request.done)
            // {
            // var png = camera.output.EncodeToPNG();
            // File.WriteAllBytesAsync(png);
            // }
            // AsyncGPUReadback.RequestIntoNativeArray(ref camera.buffer, camera.input);
            // Camera.Camera.Render();
            _buttons = default;
        }
    }

    void Update()
    {
        _buttons.a |= Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1);
        _buttons.b |= Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2);
        _buttons.c |= Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3);
        _buttons.d |= Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4);
        _buttons.left |= Input.GetKeyDown(KeyCode.LeftArrow);
        _buttons.right |= Input.GetKeyDown(KeyCode.RightArrow);
        _buttons.up |= Input.GetKeyDown(KeyCode.UpArrow);
        _buttons.down |= Input.GetKeyDown(KeyCode.DownArrow);
        _buttons.tab |= Input.GetKeyDown(KeyCode.Tab);
        _buttons.shift |= Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        _buttons.space |= Input.GetKey(KeyCode.Space);
    }

    void OnDestroy()
    {
        if (string.IsNullOrWhiteSpace(Comfy.Container)) return;
        else _ = Run("docker", $"remove {Comfy.Container} --force");
    }

    IEnumerable Serve()
    {
        var task = Run(Path.Join(Comfy.Path, "serve.sh"), "0");
        while (!task.IsCompleted) yield return null;

        var container = task.Result?.Trim();
        if (string.IsNullOrWhiteSpace(container))
            UnityEngine.Debug.LogError($"Comfy failed to start container.");
        else
        {
            UnityEngine.Debug.Log($"Comfy container serving in '{container}'.");
            Comfy.Container = container;
        }

        while (true)
        {
            if (Comfy.Generate is ComfyGeneration.Once or ComfyGeneration.Infinite)
            {
                if (Comfy.Generate is ComfyGeneration.Once) Comfy.Generate = ComfyGeneration.None;
                foreach (var item in Generate()) yield return item;
            }
            yield return null;
        }
    }

    IEnumerable Generate()
    {
        if (string.IsNullOrWhiteSpace(Comfy.Container))
        {
            UnityEngine.Debug.LogError("Missing comfy container identifier.");
            yield break;
        }

        var generateTask = Run(Path.Join(Comfy.Path, "generate.sh"), $"--container {Comfy.Container} {Option("prompt", Comfy.Prompt)} --width {Comfy.Width} --height {Comfy.Height} --steps {Comfy.Steps} --guidance {Comfy.Guidance}");
        while (!generateTask.IsCompleted) yield return null;

        var path = generateTask.Result?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            UnityEngine.Debug.LogError($"Comfy failed to generate image.");
        else
            UnityEngine.Debug.Log($"Comfy generated image at '{path}'.");

        var imageTask = File.ReadAllBytesAsync(path);
        while (!imageTask.IsCompleted) yield return null;

        var image = imageTask.Result;
        Comfy.Texture.LoadImage(image);
    }
}