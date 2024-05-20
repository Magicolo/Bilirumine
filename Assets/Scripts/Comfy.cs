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
    }

    static void Kill(string path)
    {
        try { Process.Start(new ProcessStartInfo("docker", $"compose --file '{path}' kill comfy")); } catch { }
    }

    static void Delete(string path)
    {
        try { Directory.Delete(path, true); } catch { }
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
                        UnityEngine.Debug.LogError("Failed to acquire pointer to shared memory.");
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

    static IEnumerable Wait(Task task)
    {
        while (!task.IsCompleted) yield return null;
        if (task is { Exception: { } exception }) throw exception;
    }

    static readonly (int width, int height)[] _resolutions =
    {
        (640, 384),
        (768, 512),
        (896, 640),
    };

    static int _version = 0;

    public CameraSettings Camera = new();
    public Image Flash = default!;
    public Renderer Output = default!;
    public TMP_Text Debug = default!;

    Inputs _inputs = default;

    IEnumerator Start()
    {
        var comfy = Path.Join(Application.streamingAssetsPath, "Comfy");
        var docker = Path.Join(comfy, "docker-compose.yml");
        var cache = Path.Join(comfy, "input", ".cache");
        var share = "/dev/shm/bilirumine";
        Delete(cache);
        Delete(share);
        Kill(docker);
        Directory.CreateDirectory(cache);

        using var memory = MemoryMappedFile.CreateFromFile(share, FileMode.OpenOrCreate, "bilirumine", int.MaxValue, MemoryMappedFileAccess.ReadWrite);
        Application.quitting += () => { try { memory.Dispose(); } catch { } Delete(share); };

        using var process = Process.Start(new ProcessStartInfo("docker", $"compose --file '{docker}' run --interactive --rm comfy")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        Application.quitting += () => { try { process.Kill(); } catch { } Kill(docker); };

        using var input = process.StandardInput;
        using var output = process.StandardOutput;
        using var error = process.StandardError;

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

        foreach (var item in Wait(Task.WhenAll(ReadOutput(), ReadError())))
        {
            Cursor.visible = Application.isEditor;
            yield return item;
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
            Application.quitting += () => { try { texture.Stop(); } catch { } };
            while (texture.width < 32 && texture.height < 32) yield return null;
            UnityEngine.Debug.Log($"Camera: {texture.deviceName} | Resolution: {texture.width}x{texture.height} | FPS: {texture.requestedFPS} | Graphics: {texture.graphicsFormat}");

            var (width, height) = (texture.width, texture.height);
            var buffer = new NativeArray<byte>(width * height * 4, Allocator.Persistent);
            Application.quitting += () => { try { AsyncGPUReadback.WaitAllRequests(); } catch { } };
            Application.quitting += () => { try { buffer.Dispose(); } catch { } };
            Camera.Preview.material.mainTexture = texture;

            var scale = Camera.Preview.transform.localScale;
            scale.y = (Camera.Flip, scale.y) switch { (true, > 0f) or (false, < 0f) => -scale.y, _ => scale.y };
            Camera.Preview.transform.localScale = scale;
            Camera.Preview.enabled = false;
            texture.Pause();

            while (true)
            {
                if (_inputs.Shift.Take())
                {
                    var task = WriteInput(version => new() { Version = version, Skip = true, Stop = true });
                    texture.Play();
                    yield return null;
                    Camera.Preview.enabled = true;
                    foreach (var item in Wait(task)) yield return item;
                }
                else if (Camera.Preview.enabled)
                {
                    Flash.color = Flash.color.With(a: 1f);
                    texture.Pause();
                    var request = AsyncGPUReadback.RequestIntoNativeArray(ref buffer, texture);
                    while (!request.done) yield return null;

                    var data = Convert.ToBase64String(buffer.AsReadOnlySpan());
                    pictures.Enqueue(new() { Width = width, Height = height, Data = data });
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
                    Debug.text = $@"
Images Per Second: {1f / delta.image:00.000}
Batches Per Minute: {1f / delta.batch:00.000}
Rate: {delta.speed / delta.wait:00.000}
Wait: {delta.wait:0.0000}
Speed: {delta.speed:0.0000}
Frames: {frames.Count:0000}
Resolution: {resolutions.width}x{resolutions.height}";
                }
                else
                    Debug.text = "";
                yield return null;
            }
        }

        IEnumerator UpdateState()
        {
            var (width, height) = _resolutions.Last();
            var template = new State()
            {
                Width = width,
                Height = height,
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
                    foreach (var item in Wait(task)) yield return item;
                }
                else yield return null;
            }
        }

        async Task WriteInput(Func<int, State> get)
        {
            var version = Interlocked.Increment(ref _version);
            var state = get(version);
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
            var then = (image: watch.Elapsed, batch: watch.Elapsed);
            while (!process.HasExited)
            {
                var line = await output.ReadLineAsync();
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
                    UnityEngine.Debug.Log($"Received batch: {{ Version = {version}, Loop = {loop}, Count = {count}, Width = {width}, Height = {height}, Offset = {offset}, Size = {size} }}");
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
                catch (FormatException) { UnityEngine.Debug.Log(line); }
                catch (IndexOutOfRangeException) { UnityEngine.Debug.Log(line); }
                catch (Exception exception) { UnityEngine.Debug.LogException(exception); }
            }
        }

        async Task ReadError()
        {
            while (!process.HasExited)
            {
                var line = await error.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;
                UnityEngine.Debug.Log(line);
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