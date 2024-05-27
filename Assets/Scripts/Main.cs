#nullable enable

using System.Diagnostics;
using UnityEngine;
using System.IO;
using System;
using System.Collections.Concurrent;
using System.Collections;
using System.Threading.Tasks;
using TMPro;
using System.Linq;
using UnityEngine.Rendering;
using Unity.Collections;
using UnityEngine.UI;
using System.IO.MemoryMappedFiles;
using System.Collections.Generic;
using Debug = UnityEngine.Debug;
using System.Net.Http;
using System.Text;

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

    sealed record State
    {
        public int Version;
        public int Loops;
        public Tags Tags;
        public int Width;
        public int Height;
        public int Left;
        public int Right;
        public int Bottom;
        public int Top;
        public int Zoom;
        public int Steps;
        public float Guidance;
        public float Denoise;
        public bool Full;
        public bool Break;
        public bool Stop;
        public bool Empty;
        public string? Load;
        public string? Cache;
        public string? Positive;
        public string? Negative;
        public string? Image;
        public State? Next;

        public override string ToString() =>
            $@"{{""version"":{Version},""loops"":{Loops},""tags"":{(int)Tags},""width"":{Width},""height"":{Height},""left"":{Left},""right"":{Right},""bottom"":{Bottom},""top"":{Top},""zoom"":{Zoom},""steps"":{Steps},""guidance"":{Guidance},""denoise"":{Denoise},""cache"":""{Cache}"",""full"":{(Full ? "True" : "False")},""break"":{(Break ? "True" : "False")},""stop"":{(Stop ? "True" : "False")},""empty"":{(Empty ? "True" : "False")},""positive"":""{Positive}"",""negative"":""{Negative}"",""load"":""{Load}"",""image"":""{Image}"",""next"":{Next?.ToString() ?? "None"}}}";
    }

    [Serializable]
    sealed record Word
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
        public Tags Tags;
    }

    struct Icon
    {
        public int Version;
        public int Width;
        public int Height;
        public int Offset;
        public int Size;
        public Tags Tags;
    }

    [Flags]
    public enum Tags
    {
        Frame = 1 << 0,
        Icon = 1 << 1,
        Left = 1 << 2,
        Right = 1 << 3,
        Up = 1 << 4,
        Down = 1 << 5,
    }

    // [Serializable]
    // public sealed class CameraSettings
    // {
    //     public int X = 128;
    //     public int Y = 128;
    //     public int Rate = 30;
    //     public int Device = 0;
    //     public bool Flip;
    //     public Renderer Preview = default!;
    //     public Camera Camera = default!;
    //     public WebCamTexture Texture = default!;

    //     public IEnumerable Initialize()
    //     {
    //         Debug.Log($"Camera devices: {string.Join(", ", WebCamTexture.devices.Select(device => $"{device.name}: [{string.Join(", ", device.availableResolutions ?? Array.Empty<Resolution>())}]"))}");
    //         if (!WebCamTexture.devices.TryAt(Device, out var device))
    //         {
    //             Debug.LogWarning("Camera not found.");
    //             yield break;
    //         }

    //         Texture = new WebCamTexture(device.name, X, Y, Rate)
    //         {
    //             autoFocusPoint = null,
    //             filterMode = FilterMode.Point,
    //             wrapMode = TextureWrapMode.Clamp,
    //         };
    //         Texture.Play();
    //         Application.quitting += () => { try { Texture.Stop(); } catch { } };
    //         while (Texture.width < 32 && Texture.height < 32) yield return null;
    //         Debug.Log($"Camera: {Texture.deviceName} | Resolution: {Texture.width}x{Texture.height} | FPS: {Texture.requestedFPS} | Graphics: {Texture.graphicsFormat}");
    //         Preview.material.mainTexture = Texture;

    //         var scale = Preview.transform.localScale;
    //         scale.y = (Flip, scale.y) switch { (true, > 0f) or (false, < 0f) => -scale.y, _ => scale.y };
    //         Preview.transform.localScale = scale;
    //         Preview.enabled = false;
    //         Texture.Pause();
    //     }
    // }

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

    static Process Docker(string service)
    {
        var path = Path.Join(Application.streamingAssetsPath, "docker-compose.yml");
        var process = Run(service.ToUpper(), "docker", $"compose --file '{path}' run --service-ports --interactive --rm {service}");
        Application.quitting += () => { try { Process.Start(new ProcessStartInfo("docker", $"compose --file '{path}' kill {service}")); } catch { } };
        return process;
    }

    static (Process process, HttpClient client) Ollama()
    {
        var process = Docker("ollama");
        var client = new HttpClient() { BaseAddress = new("http://localhost:11432/") };
        return (process, client);
    }

    static async IAsyncEnumerable<Word> Generate(HttpClient client, string prompt, byte[] image)
    {
        Debug.Log($"OLLAMA: Generating with prompt '{prompt}' and image of size '{image.Length}'.");
        var encoded = Convert.ToBase64String(image);
        var content = new StringContent($@"{{""model"":""llava-llama3"",""prompt"":""{prompt}"",""images"":[""{encoded}""],""stream"":true}}", Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "api/generate") { Content = content };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var word = JsonUtility.FromJson<Word>(line);
            yield return word;
            if (word.Done) break;
        }
    }

    static bool Load(MemoryMappedFile memory, int width, int height, int offset, int size, ref Texture2D? texture)
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

    static IEnumerable Wait(params Task[] tasks) => Wait(Task.WhenAll(tasks));

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
            else yield break;
        }
    }

    static readonly ((int width, int height) low, (int width, int height) medium, (int width, int height) high) _resolutions =
    (
        (640, 384),
        (768, 512),
        (896, 640)
    );

    static int _version = 0;

    public string Prompt = "Melting Train";

    // public CameraSettings Camera = new();
    public global::Icon Left = default!;
    public global::Icon Right = default!;
    public global::Icon Up = default!;
    public global::Icon Down = default!;
    public Image Flash = default!;
    public Renderer Output = default!;
    public TMP_Text Statistics = default!;

    Inputs _inputs = default;

    IEnumerator Start()
    {
        var cache = Cache();
        using var memory = Memory();
        using var comfy = Docker("comfy");
        var ollama = Ollama();
        using var __ = ollama.process;
        using var ___ = ollama.client;

        // foreach (var item in Camera.Initialize()) yield return item;

        var resolutions = (width: 0, height: 0);
        var watch = Stopwatch.StartNew();
        var delta = (image: 0.1f, batch: 5f, wait: 0.1f, speed: 1f);
        var deltas = (
            images: new ConcurrentQueue<TimeSpan>(Enumerable.Range(0, 250).Select(_ => TimeSpan.FromSeconds(delta.image))),
            batches: new ConcurrentQueue<TimeSpan>(Enumerable.Range(0, 5).Select(_ => TimeSpan.FromSeconds(delta.batch))));
        var frames = new ConcurrentQueue<Frame>();
        var icons = new ConcurrentQueue<Icon>();
        // var pictures = new ConcurrentQueue<Picture>();
        // StartCoroutine(UpdateCamera());
        StartCoroutine(UpdateFrames());
        StartCoroutine(UpdateIcons());
        StartCoroutine(UpdateState());
        StartCoroutine(UpdateDelta());
        StartCoroutine(UpdateInput());
        StartCoroutine(UpdateDebug());
        // StartCoroutine(UpdatePrompt());

        foreach (var item in Wait(ReadOutput()))
        {
            Cursor.visible = Application.isEditor;
            yield return item;
        }

        // IEnumerator UpdatePrompt()
        // {
        //     while (true)
        //     {
        //         // if (_inputs.Space.Take())
        //         // {
        //         //     Debug.Log($"GENERATE: Space");
        //         //     var task = Generate("Write 10 random words.");
        //         //     foreach (var item in Wait(Generate("Write 10 random words.")))
        //         //     {
        //         //         if (item is null) yield return item;
        //         //         else Debug.Log($"GENERATE: {item}");
        //         //     }
        //         // }
        //         yield return null;
        //     }
        // }

        // IEnumerator UpdateCamera()
        // {
        //     var (width, height) = (Camera.Texture.width, Camera.Texture.height);
        //     var buffer = Buffer(width, height);
        //     var texture = new Texture2D(width, height, TextureFormat.ARGB32, 1, true, true);
        //     while (true)
        //     {
        //         if (_inputs.Shift.Take())
        //         {
        //             var task = WriteInput(version => new() { Version = version, Skip = true, Stop = true });
        //             Camera.Texture.Play();
        //             yield return null;
        //             Camera.Preview.enabled = true;
        //             foreach (var item in Wait(task)) yield return item;
        //         }
        //         else if (Camera.Preview.enabled)
        //         {
        //             Flash.color = Flash.color.With(a: 1f);
        //             Camera.Texture.Pause();
        //             var request = AsyncGPUReadback.RequestIntoNativeArray(ref buffer, Camera.Texture);
        //             while (!request.done) yield return null;

        //             // texture.LoadRawTextureData(buffer);
        //             // texture.Apply();
        //             // Debug.Log($"OLLAMA: Generate with raw image '{buffer.Length}'.");
        //             // var encoded = texture.EncodeToPNG();
        //             // Debug.Log($"OLLAMA: Generate with encoded image '{encoded.Length}'.");
        //             // var generate = Task.Run(async () =>
        //             // {
        //             //     await foreach (var item in Generate(ollama.client, "Describe the image.", encoded))
        //             //         Debug.Log($"OLLAMA: Generated '{item}'.");
        //             // });

        //             var data = Convert.ToBase64String(buffer.AsReadOnlySpan());
        //             pictures.Enqueue(new() { Width = width, Height = height, Data = data });
        //             Camera.Preview.enabled = false;
        //             // foreach (var item in Wait(generate)) yield return item;
        //         }
        //         else
        //         {
        //             Camera.Texture.Pause();
        //             Camera.Preview.enabled = false;
        //         }
        //         yield return null;
        //     }
        // }

        IEnumerator UpdateFrames()
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
                    if (Load(memory, frame.Width, frame.Height, frame.Offset, frame.Size, ref load))
                        Output.material.mainTexture = load;
                    (main, load) = (load, main);
                    resolutions = (frame.Width, frame.Height);
                }
                else time = (double)Time.time;
                yield return null;
            }
        }

        IEnumerator UpdateIcons()
        {
            var target = (left: default(Texture2D), right: default(Texture2D), up: default(Texture2D), down: default(Texture2D));
            while (true)
            {
                if (icons.TryDequeue(out var icon))
                {
                    var area = new Rect(0f, 0f, icon.Width, icon.Height);
                    if (icon.Tags.HasFlag(Tags.Left) && Load(memory, icon.Width, icon.Height, icon.Offset, icon.Size, ref target.left))
                        Left.Content.sprite = Sprite.Create(target.left, area, area.center);
                    if (icon.Tags.HasFlag(Tags.Right) && Load(memory, icon.Width, icon.Height, icon.Offset, icon.Size, ref target.right))
                        Right.Content.sprite = Sprite.Create(target.right, area, area.center);
                    if (icon.Tags.HasFlag(Tags.Up) && Load(memory, icon.Width, icon.Height, icon.Offset, icon.Size, ref target.up))
                        Up.Content.sprite = Sprite.Create(target.up, area, area.center);
                    if (icon.Tags.HasFlag(Tags.Down) && Load(memory, icon.Width, icon.Height, icon.Offset, icon.Size, ref target.down))
                        Down.Content.sprite = Sprite.Create(target.down, area, area.center);
                }
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
            var frame = new State()
            {
                Tags = Tags.Frame,
                Cache = Application.isEditor ? null : "/input/.cache",
                Width = _resolutions.high.width,
                Height = _resolutions.high.height,
                Loops = int.MaxValue,
                Steps = 5,
                Guidance = 2.5f,
                Denoise = 0.55f,
                Positive = "(ultra detailed, oil painting, abstract, conceptual, hyper realistic, vibrant) Everything is a 'TCHOO TCHOO' train. Flesh organic locomotive speeding on vast empty nebula tracks. Eternal spiral railways in the cosmos. Coal ember engine of intricate fusion. Unholy desecrated church station. Runic glyphs neon 'TCHOO' engravings. Darkness engulfed black hole pentagram. Blood magic eldritch rituals to summon whimsy hellish trains of wonder. Everything is a 'TCHOO TCHOO' train.",
                Negative = "(nude, naked, nudity, youth, child, children, blurry, worst quality, low detail)",
                Full = true,
                Break = true,
            };
            var icon = new State()
            {
                Tags = Tags.Icon,
                Width = 512,
                Height = 512,
                Loops = 0,
                Steps = 5,
                Guidance = 5f,
                Denoise = 0.65f,
                Full = false,
                Break = false,
            };
            WriteInput(version => frame with { Version = version, Empty = true });

            var hidden = 226f;
            var speed = 5f;
            while (true)
            {
                var generate = _inputs.Space.Take();
                var prompt = $"(ultra detailed, oil painting, abstract, conceptual, hyper realistic, vibrant) Simple minimalistic figurative app icon of {Prompt}";
                UpdateIcon(Left, prompt, _inputs.Left.Take(), generate, position => position.With(x: 0f), position => position.With(x: -hidden));
                UpdateIcon(Right, prompt, _inputs.Right.Take(), generate, position => position.With(x: 0f), position => position.With(x: hidden));
                UpdateIcon(Up, prompt, _inputs.Up.Take(), generate, position => position.With(y: 0f), position => position.With(y: hidden));
                UpdateIcon(Down, prompt, _inputs.Down.Take(), generate, position => position.With(y: 0f), position => position.With(y: -hidden));
                // if (pictures.TryDequeue(out var picture))
                // {
                //     var task = WriteInput(version => template with
                //     {
                //         Version = version,
                //         Width = picture.Width,
                //         Height = picture.Height,
                //         Image = picture.Data,
                //         Next = template with { Version = version },
                //     });
                //     foreach (var item in Wait(task)) yield return item;
                // }

                var left = _inputs.Left.Take() ? 160 : 0;
                var right = _inputs.Right.Take() ? 160 : 0;
                var top = _inputs.Up.Take() ? 128 : 0;
                var bottom = _inputs.Down.Take() ? 128 : 0;
                if (left > 0 || right > 0 || top > 0 || bottom > 0)
                {
                    WriteInput(version => new[] { _resolutions.medium, _resolutions.low, _resolutions.low, _resolutions.low, _resolutions.medium }.Aggregate(
                        frame with { Version = version, Stop = true },
                        (state, resolution) => frame with
                        {
                            Version = version,
                            Width = resolution.width,
                            Height = resolution.height,
                            Left = left,
                            Right = right,
                            Top = top,
                            Bottom = bottom,
                            Stop = true,
                            Next = state
                        }));
                }
                else yield return null;
            }

            void UpdateIcon(global::Icon component, string prompt, bool move, bool generate, Func<Vector2, Vector2> show, Func<Vector2, Vector2> hide)
            {
                component.Rectangle.anchoredPosition = Vector2.Lerp(component.Rectangle.anchoredPosition, move ? show(component.Rectangle.anchoredPosition) : hide(component.Rectangle.anchoredPosition), Time.deltaTime * speed);
                component.Socket.Close.color = Color.Lerp(component.Socket.Close.color, component.Socket.Close.color.With(a: move ? Mathf.Max(1f - component.Time, 0f) : 1f), Time.deltaTime * speed);
                component.Time = move ? component.Time + Time.deltaTime : 0f;

                if (generate)
                    WriteInput(version => icon with { Version = version, Tags = icon.Tags | component.Tags, Load = component.Load, Positive = prompt });
            }
        }


        void WriteInput(Func<int, State> get)
        {
            var state = get(++_version);
            Debug.Log($"COMFY: Sending input '{state}'.");
            comfy.StandardInput.WriteLine($"{state}");
            comfy.StandardInput.Flush();
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
                    var splits = line.Split(",", StringSplitOptions.None);
                    var version = int.Parse(splits[0]);
                    var loop = int.Parse(splits[1]);
                    var tags = (Tags)int.Parse(splits[2]);
                    var width = int.Parse(splits[3]);
                    var height = int.Parse(splits[4]);
                    var count = int.Parse(splits[5]);
                    var offset = int.Parse(splits[6]);
                    var size = int.Parse(splits[7]) / count;
                    Debug.Log($"COMFY: Received output: {{ Version = {version}, Loop = {loop}, Tags = {tags}, Count = {count}, Width = {width}, Height = {height}, Offset = {offset}, Size = {size} }}");
                    if (tags.HasFlag(Tags.Frame))
                    {
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
                                Tags = tags,
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
                    if (tags.HasFlag(Tags.Icon))
                    {
                        icons.Enqueue(new Icon
                        {
                            Version = version,
                            Width = width,
                            Height = height,
                            Offset = offset,
                            Size = size,
                            Tags = tags,
                        });
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
        _inputs.Left |= Input.GetKey(KeyCode.LeftArrow);
        _inputs.Right |= Input.GetKey(KeyCode.RightArrow);
        _inputs.Up |= Input.GetKey(KeyCode.UpArrow);
        _inputs.Down |= Input.GetKey(KeyCode.DownArrow);
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