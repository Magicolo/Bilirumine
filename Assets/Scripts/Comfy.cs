#nullable enable

using System.Diagnostics;
using UnityEngine;
using System.Text.Json;
using System.IO;
using System;
using System.Collections.Concurrent;
using UnityEngine.Experimental.Rendering;
using System.Text.Json.Serialization;
using System.Collections;
using System.Threading.Tasks;
using TMPro;
using System.Linq;

public sealed class Comfy : MonoBehaviour
{
    sealed record State
    {
        public bool Run = true;
        public uint Width { get; set; }
        public uint Height { get; set; }
        public double Zoom { get; set; }
        public double Left { get; set; }
        public double Right { get; set; }
        public double Bottom { get; set; }
        public double Top { get; set; }
    }

    public bool Run = true;
    public uint Width = 768;
    public uint Height = 512;
    public int Zoom = 0;
    public int X = 0;
    public int Y = 0;
    [Range(0.5f, 2f)]
    public double Speed = 1.0;
    public Renderer Output = default!;
    public TMP_Text Debug = default!;

    (bool left, bool right, bool up, bool down, bool plus, bool minus, bool tab, bool shift, bool space) _inputs = default;

    IEnumerator Start()
    {
        var scale = Output.transform.localScale;
        scale.x = -scale.y * Width / Height;
        Output.transform.localScale = scale;

        var path = Path.Join(Application.streamingAssetsPath, "Comfy", "docker-compose.yml");
        Kill();
        var options = new JsonSerializerOptions(JsonSerializerOptions.Default)
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            WriteIndented = false,
        };
        using var process = Process.Start(new ProcessStartInfo("docker", $"compose --file '{path}' run --interactive --rm comfy")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        Application.quitting += () => { try { process.Kill(); } catch { } Kill(); };

        using var input = process.StandardInput;
        using var output = process.StandardOutput;
        using var error = process.StandardError;
        var textures = (
            main: new Texture2D((int)Width, (int)Height, GraphicsFormat.R8G8B8_UNorm, TextureCreationFlags.DontInitializePixels),
            load: new Texture2D((int)Width, (int)Height, GraphicsFormat.R8G8B8_UNorm, TextureCreationFlags.DontInitializePixels)
        );
        var watch = Stopwatch.StartNew();
        var delta = 0.1;
        var deltas = new ConcurrentQueue<TimeSpan>(Enumerable.Range(0, 256).Select(_ => TimeSpan.FromSeconds(delta)));
        var frames = new ConcurrentQueue<byte[]>();
        _ = Task.WhenAll(ReadOutput(), ReadError());
        var time = (double)Time.time;
        StartCoroutine(UpdateState());
        StartCoroutine(UpdateDelta());
        StartCoroutine(UpdateInput());
        StartCoroutine(UpdateDebug());
        while (true)
        {
            if (frames.TryDequeue(out var frame))
            {
                while (Time.time - time < delta) yield return null;
                time += delta;
                textures.load.LoadRawTextureData(frame);
                textures.load.Apply();
                textures = (textures.load, textures.main);
                Output.material.mainTexture = textures.main;
            }
            else time = (double)Time.time;
            yield return null;
        }

        IEnumerator UpdateDelta()
        {
            while (true)
            {
                if (deltas.Count > 0) delta = deltas.Select(delta => delta.TotalSeconds / Speed).Average();
                yield return null;
            }
        }

        IEnumerator UpdateDebug()
        {
            var show = Application.isEditor;
            while (true)
            {
                if (_inputs.tab.Take()) show = !show;
                if (show)
                    Debug.text = $"RATE: {1f / delta:00.00} | DELTA: {delta:0.0000} | FRAMES: {frames.Count:000} | RESOLUTION: {Width}x{Height} | ZOOM: {Zoom} | DIRECTION: ({X}, {Y})";
                else
                    Debug.text = "";
                yield return null;
            }
        }

        IEnumerator UpdateState()
        {
            var last = default(State);
            while (true)
            {
                var state = new State
                {
                    Run = Run,
                    Width = Width,
                    Height = Height,
                    Left = Math.Max(X, 0),
                    Right = Math.Min(X, 0),
                    Top = Math.Max(Y, 0),
                    Bottom = Math.Min(Y, 0),
                    Zoom = Zoom,
                };
                if (last == state) yield return null;
                else
                {
                    var task = WriteInput(state);
                    while (!task.IsCompleted) yield return null;
                    last = state;
                }
            }
        }

        IEnumerator UpdateInput()
        {
            var increment = 8;
            while (true)
            {
                if (_inputs.left.Take()) X -= increment;
                if (_inputs.right.Take()) X += increment;
                if (_inputs.up.Take()) Y += increment;
                if (_inputs.down.Take()) Y -= increment;
                if (_inputs.plus.Take()) Zoom += increment;
                if (_inputs.minus.Take() && Zoom >= increment) Zoom -= increment;
                if (_inputs.space.Take()) Run = !Run;
                yield return null;
            }
        }

        void Kill()
        {
            try { Process.Start(new ProcessStartInfo("docker", $"compose --file '{path}' kill comfy")); } catch { }
        }

        async Task WriteInput(State state)
        {
            await input.WriteLineAsync(JsonSerializer.Serialize(state, options));
            await input.FlushAsync();
        }

        async Task ReadOutput()
        {
            var then = watch.Elapsed;
            while (!process.HasExited)
            {
                var line = await output.ReadLineAsync();
                try
                {
                    var frame = Convert.FromBase64String(line);
                    if (frame.Length == Width * Height * 3)
                    {
                        var now = watch.Elapsed;
                        frames.Enqueue(frame);
                        deltas.Enqueue(now - then);
                        then = now;
                        while (deltas.Count > 256) deltas.TryDequeue(out _);
                    }
                    else
                        UnityEngine.Debug.Log(line);
                }
                catch (FormatException) { UnityEngine.Debug.Log(line); }
                catch (Exception exception) { UnityEngine.Debug.LogException(exception); }
            }
        }

        async Task ReadError()
        {
            while (!process.HasExited) UnityEngine.Debug.Log(await error.ReadLineAsync());
        }
    }

    void Update()
    {
        _inputs.left |= Input.GetKeyDown(KeyCode.LeftArrow);
        _inputs.right |= Input.GetKeyDown(KeyCode.RightArrow);
        _inputs.up |= Input.GetKeyDown(KeyCode.UpArrow);
        _inputs.down |= Input.GetKeyDown(KeyCode.DownArrow);
        _inputs.plus |= Input.GetKeyDown(KeyCode.Plus) || Input.GetKeyDown(KeyCode.KeypadPlus) || Input.GetKeyDown(KeyCode.Equals);
        _inputs.minus |= Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus) || Input.GetKeyDown(KeyCode.Underscore);
        _inputs.tab |= Input.GetKeyDown(KeyCode.Tab);
        _inputs.shift |= Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        _inputs.space |= Input.GetKeyDown(KeyCode.Space);
    }
}