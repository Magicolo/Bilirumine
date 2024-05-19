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
using System.Threading;
using UnityEngine.Rendering;
using Unity.Collections;

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
        public bool _1;
        public bool _2;
        public bool _3;
        public bool _4;
    }

    record State
    {
        public int Version;
        public int Width;
        public int Height;
        public int Left;
        public int Right;
        public int Bottom;
        public int Top;
        public int Zoom;
        public bool Stop;
        public string Cache = Application.isEditor ? "memory" : "disk";
        public string Positive = "(ultra detailed, oil painting, abstract, conceptual, hyper realistic, vibrant) Everything is a 'TCHOO TCHOO' train. Flesh organic locomotive speeding on vast empty nebula tracks. Eternal spiral railways in the cosmos. Coal ember engine of intricate fusion. Unholy desecrated church station. Runic glyphs neon 'TCHOO' engravings. Darkness engulfed black hole pentagram. Blood magic eldritch rituals to summon whimsy hellish trains of wonder. Everything is a 'TCHOO TCHOO' train.";
        public string Negative = "(nude, naked, child, children, blurry, worst quality, low detail, monochrome, simple, centered)";
        public string? Image;
        public State? Next;

        public override string ToString() =>
            $@"{{""version"":{Version},""width"":{Width},""height"":{Height},""left"":{Left},""right"":{Right},""bottom"":{Bottom},""top"":{Top},""zoom"":{Zoom},""cache"":""{Cache}"",""stop"":{(Stop ? "True" : "False")},""positive"":""{Positive}"",""negative"":""{Negative}"",""image"":{(Image is null ? "None" : @$"""{Image}""")},""next"":{Next?.ToString() ?? "None"}}}";
    }

    struct Picture
    {
        public int Width;
        public int Height;
        public string Data;
    }

    struct Frame
    {
        public int Version;
        public int Width;
        public int Height;
        public byte[] Data;
    }

    [Serializable]
    public sealed class CameraSettings
    {
        public int X = 128;
        public int Y = 128;
        public int Rate = 30;
        public int Device = 0;
        public bool Flip;
        public Renderer Preview = default!;
        public Camera Camera = default!;
    }

    static void Kill(string path)
    {
        try { Process.Start(new ProcessStartInfo("docker", $"compose --file '{path}' kill comfy")); } catch { }
    }

    static readonly (int width, int height)[] _resolutions =
    {
        (640, 384),
        (768, 512),
        (896, 640),
        // (1024, 768),
    };

    static int _version = 0;

    public CameraSettings Camera = new();
    public Renderer Output = default!;
    public TMP_Text Debug = default!;

    Inputs _inputs = default;

    IEnumerator Start()
    {
        var path = Path.Join(Application.streamingAssetsPath, "Comfy", "docker-compose.yml");
        Kill(path);
        using var process = Process.Start(new ProcessStartInfo("docker", $"compose --file '{path}' run --interactive --rm comfy")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        Application.quitting += () => { try { process.Kill(); } catch { } Kill(path); };

        using var input = process.StandardInput;
        using var output = process.StandardOutput;
        using var error = process.StandardError;

        var resolutions = (current: (width: 0, height: 0), next: (width: 0, height: 0));
        var watch = Stopwatch.StartNew();
        var delta = 0.1f;
        var deltas = new ConcurrentQueue<TimeSpan>(Enumerable.Range(0, 250).Select(_ => TimeSpan.FromSeconds(delta)));
        var frames = new ConcurrentQueue<Frame>();
        var pictures = new ConcurrentQueue<Picture>();
        _ = Task.WhenAll(ReadOutput(), ReadError());
        var speed = 1f;
        StartCoroutine(UpdateCamera());
        StartCoroutine(UpdateTexture());
        StartCoroutine(UpdateState());
        StartCoroutine(UpdateDelta());
        StartCoroutine(UpdateInput());
        StartCoroutine(UpdateDebug());

        while (true)
        {
            Cursor.visible = Application.isEditor;
            yield return null;
        }

        IEnumerator UpdateCamera()
        {
            UnityEngine.Debug.Log($"Camera devices: {string.Join(", ", WebCamTexture.devices.Select(device => $"{device.name}: [{string.Join(", ", device.availableResolutions ?? Array.Empty<Resolution>())}]"))}");
            if (!WebCamTexture.devices.TryAt(Camera.Device, out var device))
            {
                UnityEngine.Debug.LogWarning("Camera not found.");
                yield break;
            }

            var texture = new WebCamTexture(device.name, Camera.X, Camera.Y, Camera.Rate)
            {
                autoFocusPoint = null,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.Play();
            Application.quitting += texture.Stop;
            while (texture.width < 32 && texture.height < 32) yield return null;
            UnityEngine.Debug.Log($"Camera: {texture.deviceName} | Resolution: {texture.width}x{texture.height} | FPS: {texture.requestedFPS} | Graphics: {texture.graphicsFormat}");

            var (width, height) = (texture.width, texture.height);
            var buffer = new NativeArray<byte>(width * height * sizeof(float) * 3, Allocator.Persistent);
            Application.quitting += AsyncGPUReadback.WaitAllRequests;
            Application.quitting += buffer.Dispose;
            Camera.Preview.material.mainTexture = texture;

            var scale = Camera.Preview.transform.localScale;
            scale.y = (Camera.Flip, scale.y) switch { (true, > 0f) or (false, < 0f) => -scale.y, _ => scale.y };
            Camera.Preview.transform.localScale = scale;
            while (true)
            {
                if (_inputs.Shift.Take())
                {
                    texture.Play();
                    Camera.Preview.enabled = true;
                }
                else if (Camera.Preview.enabled)
                {
                    texture.Pause();
                    var task = Task.Run(async () =>
                    {
                        await AsyncGPUReadback.RequestIntoNativeArrayAsync(ref buffer, texture);
                        var image = Convert.ToBase64String(buffer.AsReadOnlySpan());
                        pictures.Enqueue(new() { Width = width, Height = height, Data = image });
                    });
                    // TODO: Flash!
                    while (!task.IsCompleted) yield return null;
                    Camera.Preview.enabled = false;
                }
                else
                {
                    texture.Pause();
                    Camera.Preview.enabled = false;
                }
                yield return null;
            }
        }

        IEnumerator UpdateTexture()
        {
            var time = (double)Time.time;
            var main = default(Texture2D);
            var load = default(Texture2D);
            while (true)
            {
                if (frames.TryDequeue(out var frame))
                {
                    while (Time.time - time < delta / speed) yield return null;
                    time += delta / speed;

                    if (load == null || load.width != frame.Width || load.height != frame.Height)
                        load = new Texture2D(frame.Width, frame.Height, GraphicsFormat.R8G8B8_UNorm, TextureCreationFlags.DontInitializePixels);

                    load.LoadRawTextureData(frame.Data);
                    load.Apply();
                    Output.material.mainTexture = load;
                    (main, load) = (load, main);
                    resolutions.current = (frame.Width, frame.Height);
                }
                else time = (double)Time.time;
                yield return null;
            }
        }

        IEnumerator UpdateDelta()
        {
            while (true)
            {
                if (deltas.Count > 0) delta = Mathf.Lerp(delta, deltas.Select(delta => (float)delta.TotalSeconds).Average(), Time.deltaTime);
                if (frames.Count > 0) speed = Mathf.Lerp(speed, Mathf.Clamp(frames.Count / (5f / delta), 0.75f, 1.5f), Time.deltaTime);
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
                {
                    Debug.text = $@"
Rate: {1f / delta:00.00}
Delta: {delta:0.0000}
Speed: {speed:0.0000}
Frames: {frames.Count:000}
Resolution: {resolutions.current.width}x{resolutions.current.height}{(resolutions.current == resolutions.next ? $"" : $"-> {resolutions.next.width}x{resolutions.next.height}")}";
                }
                else
                    Debug.text = "";
                yield return null;
            }
        }

        IEnumerator UpdateState()
        {
            // var old = default((int, int, int, int, int, int, int));
            var first = true;
            var (width, height) = _resolutions.Last();
            var template = new State()
            {
                Width = width,
                Height = height,
                Zoom = 32
            };
            while (true)
            {
                if (first.Take())
                {
                    var task = WriteInput(version => template with { Version = version });
                    while (!task.IsCompleted) yield return null;
                }
                else if (pictures.TryDequeue(out var picture))
                {
                    var task = WriteInput(version => template with
                    {
                        Version = version,
                        Width = picture.Width,
                        Height = picture.Height,
                        Image = picture.Data,
                        Next = template with { Version = version },
                    });
                    while (!task.IsCompleted) yield return null;
                }
                else if (_inputs.Left.Take())
                {
                    var task = WriteInput(version => _resolutions.Reverse().Concat(_resolutions).Aggregate(
                        template with { Version = version, Stop = true },
                        (state, resolution) => template with
                        {
                            Version = version,
                            Width = resolution.width,
                            Height = resolution.height,
                            Left = 192,
                            Stop = true,
                            Next = state
                        }));
                    while (!task.IsCompleted) yield return null;
                }
                else yield return null;
            }
        }

        async Task WriteInput(Func<int, State> get)
        {
            var version = Interlocked.Increment(ref _version);
            var state = get(version);
            resolutions.next = (state.Width, state.Height);
            await input.WriteLineAsync($"{state}");
            await input.FlushAsync();
        }

        IEnumerator UpdateInput()
        {
            while (true)
            {
                // var increment = 8;
                // var horizontal = Width / 4;
                // var vertical = Height / 4;

                // if (_inputs.Left.Take()) x = -horizontal;
                // else if (_inputs.Right.Take()) x = horizontal;
                // else x = 0;

                // if (_inputs.Down.Take()) y = -vertical;
                // else if (_inputs.Up.Take()) y = vertical;
                // else y = 0;

                // if (_inputs.Plus.Take()) Zoom += increment;
                // if (_inputs.Minus.Take() && Zoom >= increment) Zoom -= increment;

                // if (_inputs._1.Take()) Resolution = 0;
                // else if (_inputs._2.Take()) Resolution = 1;
                // else if (_inputs._3.Take()) Resolution = 2;
                // else if (_inputs._4.Take()) Resolution = 3;
                yield return null;
            }
        }

        async Task ReadOutput()
        {
            var then = watch.Elapsed;
            while (!process.HasExited)
            {
                var line = await output.ReadLineAsync();
                try
                {
                    var splits = line.Split(",");
                    var frame = new Frame
                    {
                        Version = int.Parse(splits[0]),
                        Width = int.Parse(splits[1]),
                        Height = int.Parse(splits[2]),
                        Data = Convert.FromBase64String(splits[3])
                    };
                    var now = watch.Elapsed;
                    frames.Enqueue(frame);
                    deltas.Enqueue(now - then);
                    then = now;
                    while (deltas.Count > 250) deltas.TryDequeue(out _);
                }
                catch (FormatException) { UnityEngine.Debug.Log(line); }
                catch (IndexOutOfRangeException) { UnityEngine.Debug.Log(line); }
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
        _inputs.Left |= Input.GetKeyDown(KeyCode.LeftArrow);
        _inputs.Right |= Input.GetKeyDown(KeyCode.RightArrow);
        _inputs.Up |= Input.GetKeyDown(KeyCode.UpArrow);
        _inputs.Down |= Input.GetKeyDown(KeyCode.DownArrow);
        _inputs.Plus |= Input.GetKeyDown(KeyCode.Plus) || Input.GetKeyDown(KeyCode.KeypadPlus) || Input.GetKeyDown(KeyCode.Equals);
        _inputs.Minus |= Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus) || Input.GetKeyDown(KeyCode.Underscore);
        _inputs.Tab |= Input.GetKeyDown(KeyCode.Tab);
        _inputs.Shift |= Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        _inputs.Space |= Input.GetKeyDown(KeyCode.Space);
        _inputs._1 |= Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1);
        _inputs._2 |= Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2);
        _inputs._3 |= Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3);
        _inputs._4 |= Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4);
    }
}