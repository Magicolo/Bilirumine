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
using UnityEngine.UI;
using System.IO.MemoryMappedFiles;
using System.Collections.Generic;
using Debug = UnityEngine.Debug;

public sealed class Main : MonoBehaviour
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
        public int Batch;
        public int Width;
        public int Height;
        public int Left;
        public int Right;
        public int Bottom;
        public int Top;
        public int Zoom;
        public bool Skip;
        public bool Stop;
        public string Cache = "";
        public string Positive = "";
        public string Negative = "";
        public string Image = "";
        public State? Next;

        public override string ToString() =>
            $@"{{""version"":{Version},""width"":{Width},""height"":{Height},""left"":{Left},""right"":{Right},""bottom"":{Bottom},""top"":{Top},""zoom"":{Zoom},""cache"":""{Cache}"",""stop"":{(Stop ? "True" : "False")},""skip"":{(Skip ? "True" : "False")},""positive"":""{Positive}"",""negative"":""{Negative}"",""image"":""{Image}"",""next"":{Next?.ToString() ?? "None"}}}";
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
        public int Loop;
        public int Index;
        public int Count;
        public int Width;
        public int Height;
        public int Offset;
        public int Size;
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
        public WebCamTexture Texture = default!;

        public IEnumerable Initialize()
        {
            Debug.Log($"Camera devices: {string.Join(", ", WebCamTexture.devices.Select(device => $"{device.name}: [{string.Join(", ", device.availableResolutions ?? Array.Empty<Resolution>())}]"))}");
            if (!WebCamTexture.devices.TryAt(Device, out var device))
            {
                Debug.LogWarning("Camera not found.");
                yield break;
            }

            Texture = new WebCamTexture(device.name, X, Y, Rate)
            {
                autoFocusPoint = null,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            Texture.Play();
            Application.quitting += () => { try { Texture.Stop(); } catch { } };
            while (Texture.width < 32 && Texture.height < 32) yield return null;
            Debug.Log($"Camera: {Texture.deviceName} | Resolution: {Texture.width}x{Texture.height} | FPS: {Texture.requestedFPS} | Graphics: {Texture.graphicsFormat}");
            Preview.material.mainTexture = Texture;

            var scale = Preview.transform.localScale;
            scale.y = (Flip, scale.y) switch { (true, > 0f) or (false, < 0f) => -scale.y, _ => scale.y };
            Preview.transform.localScale = scale;
            Preview.enabled = false;
            Texture.Pause();
        }
    }

    static MemoryMappedFile Memory()
    {
        var path = "/dev/shm/bilirumine";
        var memory = MemoryMappedFile.CreateFromFile(path, FileMode.OpenOrCreate, "bilirumine", int.MaxValue, MemoryMappedFileAccess.ReadWrite);
        Application.quitting += () => { try { memory.Dispose(); } catch { } };
        Application.quitting += () => { try { File.Delete(path); } catch { } };
        return memory;
    }

    static string Cache()
    {
        var path = Path.Join(Application.streamingAssetsPath, "input", ".cache");
        try { Directory.Delete(path, true); } catch { }
        Directory.CreateDirectory(path);
        Application.quitting += () => { try { Directory.Delete(path, true); } catch { } };
        return path;
    }

    static Process Run(string name, string command, string arguments)
    {
        var process = Process.Start(new ProcessStartInfo(command, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
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

    static Process Docker(string service)
    {
        var path = Path.Join(Application.streamingAssetsPath, "docker-compose.yml");
        var process = Run(service.ToUpper(), "docker", $"compose --file '{path}' run --interactive --rm {service}");
        Application.quitting += () => { try { Process.Start(new ProcessStartInfo("docker", $"compose --file '{path}' kill {service}")); } catch { } };
        return process;
    }

    static async IAsyncEnumerable<string> Ollama(string prompt)
    {
        var path = Path.Join(Application.streamingAssetsPath, "docker-compose.yml");
        using var process = Run("OLLAMA", "docker", $"compose --file '{path}' exec ollama run llava-llama3 '{prompt}'");
        var buffer = new char[256];
        while (!process.HasExited)
        {
            var count = await process.StandardOutput.ReadAsync(buffer);
            yield return new string(buffer, 0, count);
        }
    }

    static void Load(MemoryMappedFile memory, Frame frame, Texture2D target)
    {
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
                        return;
                    }
                    var source = (IntPtr)(pointer + frame.Offset);
                    target.LoadRawTextureData(source, frame.Size);
                }
                finally { access.SafeMemoryMappedViewHandle.ReleasePointer(); }
            }
        }
        target.Apply();
    }

    static NativeArray<byte> Buffer(int width, int height)
    {
        var buffer = new NativeArray<byte>(width * height * 4, Allocator.Persistent);
        Application.quitting += () => { try { AsyncGPUReadback.WaitAllRequests(); } catch { } };
        Application.quitting += () => { try { buffer.Dispose(); } catch { } };
        return buffer;
    }

    static IEnumerable Wait(Task task)
    {
        while (!task.IsCompleted) yield return null;
        if (task is { Exception: { } exception }) throw exception;
    }

    static IEnumerable Wait(ValueTask task)
    {
        while (!task.IsCompleted) yield return null;
    }

    static IEnumerable Wait<T>(ValueTask<T> task)
    {
        while (!task.IsCompleted) yield return null;
    }

    static IEnumerable<T?> Wait<T>(IAsyncEnumerable<T> source) where T : class
    {
        var enumerator = source.GetAsyncEnumerator();
        while (true)
        {
            var move = enumerator.MoveNextAsync();
            foreach (var _ in Wait(move)) yield return null;
            if (move.Result) yield return enumerator.Current;
            else break;
        }
    }

    static readonly ((int width, int height) low, (int width, int height) medium, (int width, int height) high) _resolutions =
    (
        (640, 384),
        (768, 512),
        (896, 640)
    );

    static int _version = 0;

    public CameraSettings Camera = new();
    public Image Flash = default!;
    public Renderer Output = default!;
    public TMP_Text Statistics = default!;

    Inputs _inputs = default;

    IEnumerator Start()
    {
        var cache = Cache();
        using var memory = Memory();
        using var comfy = Docker("comfy");
        using var ollama = Docker("ollama");
        foreach (var item in Camera.Initialize()) yield return item;

        var resolutions = (width: 0, height: 0);
        var watch = Stopwatch.StartNew();
        var delta = (image: 0.1f, batch: 5f, wait: 0.1f, speed: 1f);
        var deltas = (
            images: new ConcurrentQueue<TimeSpan>(Enumerable.Range(0, 250).Select(_ => TimeSpan.FromSeconds(delta.image))),
            batches: new ConcurrentQueue<TimeSpan>(Enumerable.Range(0, 5).Select(_ => TimeSpan.FromSeconds(delta.batch))));
        var frames = new ConcurrentQueue<Frame>();
        var pictures = new ConcurrentQueue<Picture>();
        StartCoroutine(UpdateCamera());
        StartCoroutine(UpdateTexture());
        StartCoroutine(UpdateState());
        StartCoroutine(UpdateDelta());
        StartCoroutine(UpdateInput());
        StartCoroutine(UpdateDebug());
        StartCoroutine(UpdatePrompt());

        foreach (var item in Wait(ReadOutput()))
        {
            Cursor.visible = Application.isEditor;
            yield return item;
        }

        IEnumerator UpdatePrompt()
        {
            while (true)
            {
                if (_inputs.Space.Take())
                {
                    foreach (var item in Wait(Ollama("Write 10 random words.")))
                    {
                        if (item is null) yield return null;
                        else Debug.Log(item);
                    }
                }
                yield return null;
            }
        }

        IEnumerator UpdateCamera()
        {
            var (width, height) = (Camera.Texture.width, Camera.Texture.height);
            var buffer = Buffer(width, height);
            while (true)
            {
                if (_inputs.Shift.Take())
                {
                    var task = WriteInput(version => new() { Version = version, Skip = true, Stop = true });
                    Camera.Texture.Play();
                    yield return null;
                    Camera.Preview.enabled = true;
                    foreach (var item in Wait(task)) yield return item;
                }
                else if (Camera.Preview.enabled)
                {
                    Flash.color = Flash.color.With(a: 1f);
                    Camera.Texture.Pause();
                    var request = AsyncGPUReadback.RequestIntoNativeArray(ref buffer, Camera.Texture);
                    while (!request.done) yield return null;

                    var data = Convert.ToBase64String(buffer.AsReadOnlySpan());
                    pictures.Enqueue(new() { Width = width, Height = height, Data = data });
                    Camera.Preview.enabled = false;
                }
                else
                {
                    Camera.Texture.Pause();
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
                    while (Time.time - time < delta.wait / delta.speed) yield return null;
                    time += delta.wait / delta.speed;

                    if (load == null || load.width != frame.Width || load.height != frame.Height)
                        load = new Texture2D(frame.Width, frame.Height, GraphicsFormat.R8G8B8_UNorm, TextureCreationFlags.DontInitializePixels);

                    Load(memory, frame, load);
                    Output.material.mainTexture = load;
                    (main, load) = (load, main);
                    resolutions = (frame.Width, frame.Height);
                }
                else time = (double)Time.time;
                yield return null;
            }
        }

        IEnumerator UpdateDelta()
        {
            while (true)
            {
                delta.image = deltas.images.Select(delta => (float)delta.TotalSeconds).Average();
                delta.batch = deltas.batches.Select(delta => (float)delta.TotalMinutes).Average();
                delta.wait = Mathf.Lerp(delta.wait, delta.image, Time.deltaTime);

                var rate = 10f / delta.wait;
                var ratio = Mathf.Pow(Mathf.Clamp01(frames.Count / rate), 2f);
                delta.speed = Mathf.Lerp(delta.speed, 0.75f + ratio / 2f, Time.deltaTime);
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
                    Statistics.text = $@"
Images Per Second: {1f / delta.image:00.000}
Batches Per Minute: {1f / delta.batch:00.000}
Rate: {delta.speed / delta.wait:00.000}
Wait: {delta.wait:0.0000}
Speed: {delta.speed:0.0000}
Frames: {frames.Count:0000}
Resolution: {resolutions.width}x{resolutions.height}";
                }
                else
                    Statistics.text = "";
                yield return null;
            }
        }

        IEnumerator UpdateState()
        {
            var template = new State()
            {
                Width = _resolutions.high.width,
                Height = _resolutions.high.height,
                Cache = Application.isEditor ? "" : "/input/.cache",
                Positive = "(ultra detailed, oil painting, abstract, conceptual, hyper realistic, vibrant) Everything is a 'TCHOO TCHOO' train. Flesh organic locomotive speeding on vast empty nebula tracks. Eternal spiral railways in the cosmos. Coal ember engine of intricate fusion. Unholy desecrated church station. Runic glyphs neon 'TCHOO' engravings. Darkness engulfed black hole pentagram. Blood magic eldritch rituals to summon whimsy hellish trains of wonder. Everything is a 'TCHOO TCHOO' train.",
                Negative = "(nude, naked, child, children, blurry, worst quality, low detail, monochrome, simple, centered)",
                Zoom = 64
            };
            while (true)
            {
                if (pictures.TryDequeue(out var picture))
                {
                    var task = WriteInput(version => template with
                    {
                        Version = version,
                        Width = picture.Width,
                        Height = picture.Height,
                        Image = picture.Data,
                        Next = template with { Version = version },
                    });
                    foreach (var item in Wait(task)) yield return item;
                }

                var left = _inputs.Left.Take() ? 160 : 0;
                var right = _inputs.Right.Take() ? 160 : 0;
                var top = _inputs.Up.Take() ? 128 : 0;
                var bottom = _inputs.Down.Take() ? 128 : 0;
                if (left > 0 || right > 0 || top > 0 || bottom > 0)
                {
                    var task = WriteInput(version => new[] { _resolutions.medium, _resolutions.low, _resolutions.low, _resolutions.low, _resolutions.medium }.Aggregate(
                        template with { Version = version, Stop = true },
                        (state, resolution) => template with
                        {
                            Version = version,
                            Width = resolution.width,
                            Height = resolution.height,
                            Left = left,
                            Right = right,
                            Top = top,
                            Bottom = bottom,
                            Zoom = 0,
                            Stop = true,
                            Next = state
                        }));
                    foreach (var item in Wait(task)) yield return item;
                }
                else yield return null;
            }
        }

        async Task WriteInput(Func<int, State> get)
        {
            var version = Interlocked.Increment(ref _version);
            var state = get(version);
            await comfy.StandardInput.WriteLineAsync($"{state}");
            await comfy.StandardInput.FlushAsync();
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
            var then = (image: watch.Elapsed, batch: watch.Elapsed);
            while (!comfy.HasExited)
            {
                var line = await comfy.StandardOutput.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var splits = line.Split(",");
                    var version = int.Parse(splits[0]);
                    var loop = int.Parse(splits[1]);
                    var width = int.Parse(splits[2]);
                    var height = int.Parse(splits[3]);
                    var count = int.Parse(splits[4]);
                    var offset = int.Parse(splits[5]);
                    var size = int.Parse(splits[6]) / count;
                    Debug.Log($"Received batch: {{ Version = {version}, Loop = {loop}, Count = {count}, Width = {width}, Height = {height}, Offset = {offset}, Size = {size} }}");
                    for (int i = 0; i < count; i++, offset += size)
                    {
                        frames.Enqueue(new Frame
                        {
                            Version = version,
                            Loop = loop,
                            Index = i,
                            Count = count,
                            Width = width,
                            Height = height,
                            Offset = offset,
                            Size = size,
                        });

                        var now = watch.Elapsed;
                        if (deltas.images.TryDequeue(out _)) deltas.images.Enqueue(now - then.image);
                        then.image = now;
                    }
                    {
                        var now = watch.Elapsed;
                        if (deltas.batches.TryDequeue(out _)) deltas.batches.Enqueue(now - then.batch);
                        then.batch = now;
                    }
                }
                catch (FormatException) { Debug.Log($"COMFY: {line}"); }
                catch (IndexOutOfRangeException) { Debug.Log($"COMFY: {line}"); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
        }
    }

    void Update()
    {
        Flash.color = Flash.color.With(a: Mathf.Lerp(Flash.color.a, 0f, Time.deltaTime * 5f));
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