#nullable enable

using System.Diagnostics;
using UnityEngine;
using System.IO;
using System;
using System.Collections.Concurrent;
using UnityEngine.Experimental.Rendering;
using System.Collections;
using System.Threading.Tasks;
using TMPro;
using System.Linq;

/*
    TODO:
    - When moving, drop the resolution to level 0.
        - Ideally, accelerate by going through levels 4-3-2-1-0 for a seemless decrease in resolution.
        - Conversly decelerate by going through levels 0-1-2-3-4 for a seemless increase in resolution.
        - Note that changing the resolution will result in a 'natural' acceleration/deceleration.

*/
public sealed class Comfy : MonoBehaviour
{
    struct Inputs
    {
        public bool Left;
        public bool Right;
        public bool Up;
        public bool Down;
        public bool Plus;
        public bool Minus;
        public bool Tab;
        public bool Shift;
        public bool Space;
        public bool _0;
        public bool _1;
        public bool _2;
        public bool _3;
        public bool _4;
    }
    sealed record State
    {
        public int Identifier { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Zoom { get; set; }
        public double Left { get; set; }
        public double Right { get; set; }
        public double Bottom { get; set; }
        public double Top { get; set; }
        public bool Stop { get; set; }
    }

    public int Width = 768;
    public int Height = 512;
    public int Zoom = 0;
    public Renderer Output = default!;
    public TMP_Text Debug = default!;

    Inputs _inputs = default;

    IEnumerator Start()
    {
        var x = 0;
        var y = 0;
        var path = Path.Join(Application.streamingAssetsPath, "Comfy", "docker-compose.yml");
        Kill();
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

        var watch = Stopwatch.StartNew();
        var delta = 0.1f;
        var deltas = new ConcurrentQueue<TimeSpan>(Enumerable.Range(0, 256).Select(_ => TimeSpan.FromSeconds(delta)));
        var frames = new ConcurrentQueue<byte[]>();
        _ = Task.WhenAll(ReadOutput(), ReadError());
        var speed = 1f;
        StartCoroutine(UpdateTexture());
        StartCoroutine(UpdateState());
        StartCoroutine(UpdateDelta());
        StartCoroutine(UpdateInput());
        StartCoroutine(UpdateDebug());
        while (true) yield return null;

        IEnumerator UpdateTexture()
        {
            var time = (double)Time.time;
            var format = GraphicsFormat.R8G8B8_UNorm;
            var flag = TextureCreationFlags.DontInitializePixels;
            var main = new Texture2D(Width, Height, format, flag);
            var load = new Texture2D(Width, Height, format, flag);
            while (true)
            {
                if (frames.TryDequeue(out var frame))
                {
                    while (Time.time - time < delta / speed) yield return null;
                    time += delta / speed;

                    if (load.width != Width || load.height != Height)
                    {
                        if (load.width * load.height * 3 != frame.Length)
                        {
                            load = new Texture2D(Width, Height, format, flag);
                            var scale = Output.transform.localScale;
                            scale.x = -scale.y * Width / Height;
                            Output.transform.localScale = scale;
                        }
                    }
                    load.LoadRawTextureData(frame);
                    load.Apply();
                    Output.material.mainTexture = load;
                    (main, load) = (load, main);
                }
                else time = (double)Time.time;
                yield return null;
            }
        }

        IEnumerator UpdateDelta()
        {
            while (true)
            {
                if (deltas.Count > 0) delta = deltas.Select(delta => (float)delta.TotalSeconds).Average();
                if (frames.Count > 0) speed = Mathf.Lerp(speed, Mathf.Clamp(frames.Count / (2.5f / delta), 0.75f, 1.5f), Time.deltaTime / 5);
                yield return null;
            }
        }

        IEnumerator UpdateDebug()
        {
            var show = Application.isEditor;
            while (true)
            {
                if (_inputs.Tab.Take()) show = !show;
                if (show)
                    Debug.text = $@"
Rate: {1f / delta:00.00}
Delta: {delta:0.0000}
Speed: {speed:0.0000}
Frames: {frames.Count:000}
Resolution: {Width}x{Height}
Zoom: {Zoom}
Direction: ({x}, {y})";
                else
                    Debug.text = "";
                yield return null;
            }
        }

        IEnumerator UpdateState()
        {
            var counter = 0;
            var old = default((int, int, int, int, int, int, int, int));
            while (true)
            {
                var @new = (
                    width: Width,
                    height: Height,
                    left: Math.Max(-x, 0),
                    right: Math.Max(x, 0),
                    bottom: Math.Max(-y, 0),
                    top: Math.Max(y, 0),
                    zoom: Zoom,
                    stop: 0
                );
                if (old == @new) yield return null;
                else
                {
                    var task = Task.Run(async () =>
                    {
                        var line = $@"{{""identifier"":{++counter},""width"":{@new.width},""height"":{@new.height},""left"":{@new.left},""right"":{@new.right},""bottom"":{@new.bottom},""top"":{@new.top},""zoom"":{@new.zoom},""stop"":{@new.stop}}}";
                        await input.WriteLineAsync(line);
                        await input.FlushAsync();
                    });
                    while (!task.IsCompleted) yield return null;
                    old = @new;
                }
            }
        }

        IEnumerator UpdateInput()
        {
            while (true)
            {
                var increment = 8;
                var horizontal = Width / 4;
                var vertical = Height / 4;

                if (_inputs.Left.Take()) x = -horizontal;
                else if (_inputs.Right.Take()) x = horizontal;
                else x = 0;

                if (_inputs.Down.Take()) y = -vertical;
                else if (_inputs.Up.Take()) y = vertical;
                else y = 0;

                if (_inputs.Plus.Take()) Zoom += increment;
                if (_inputs.Minus.Take() && Zoom >= increment) Zoom -= increment;

                if (_inputs._0.Take()) (Width, Height) = (512, 256);
                else if (_inputs._1.Take()) (Width, Height) = (640, 384);
                else if (_inputs._2.Take()) (Width, Height) = (768, 512);
                else if (_inputs._3.Take()) (Width, Height) = (896, 640);
                else if (_inputs._4.Take()) (Width, Height) = (1024, 768);
                yield return null;
            }
        }

        void Kill()
        {
            try { Process.Start(new ProcessStartInfo("docker", $"compose --file '{path}' kill comfy")); } catch { }
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
                    if (frame.Length >= 256 * 256)
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
        _inputs.Left |= Input.GetKey(KeyCode.LeftArrow);
        _inputs.Right |= Input.GetKey(KeyCode.RightArrow);
        _inputs.Up |= Input.GetKey(KeyCode.UpArrow);
        _inputs.Down |= Input.GetKey(KeyCode.DownArrow);
        _inputs.Plus |= Input.GetKeyDown(KeyCode.Plus) || Input.GetKeyDown(KeyCode.KeypadPlus) || Input.GetKeyDown(KeyCode.Equals);
        _inputs.Minus |= Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus) || Input.GetKeyDown(KeyCode.Underscore);
        _inputs.Tab |= Input.GetKeyDown(KeyCode.Tab);
        _inputs.Shift |= Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        _inputs.Space |= Input.GetKeyDown(KeyCode.Space);
        _inputs._0 |= Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0);
        _inputs._1 |= Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1);
        _inputs._2 |= Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2);
        _inputs._3 |= Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3);
        _inputs._4 |= Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4);
    }
}