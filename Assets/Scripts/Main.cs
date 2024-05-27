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
using System.Threading;
using Random = UnityEngine.Random;

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
    sealed record GenerateRequest
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
    sealed record GenerateResponse
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
        public string Description;
        public int[] Context;
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

    static async IAsyncEnumerable<GenerateResponse> Generate(HttpClient client, string prompt, byte[] image)
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
            var word = JsonUtility.FromJson<GenerateResponse>(line);
            yield return word;
            if (word.Done) break;
        }
    }

    static async Task<(string description, int[] context)> Generate(HttpClient client, string prompt, int[] context)
    {
        var json = JsonUtility.ToJson(new GenerateRequest
        {
            Model = "llama3",
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

    static bool Load(MemoryMappedFile memory, global::Icon component, Icon icon)
    {
        if (icon.Tags.HasFlag(component.Tags) && Load(memory, icon.Width, icon.Height, icon.Offset, icon.Size, ref component.Texture))
        {
            var area = new Rect(0f, 0f, icon.Width, icon.Height);
            component.Image.sprite = Sprite.Create(component.Texture, area, area.center);
            component.Description = icon.Description;
            return true;
        }
        else
            return false;
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

    public global::Icon Left = default!;
    public global::Icon Right = default!;
    public global::Icon Up = default!;
    public global::Icon Down = default!;
    public RectTransform Center = default!;
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

        var resolutions = (width: 0, height: 0);
        var watch = Stopwatch.StartNew();
        var delta = (image: 0.1f, batch: 5f, wait: 0.1f, speed: 1f);
        var deltas = (
            images: new ConcurrentQueue<TimeSpan>(Enumerable.Range(0, 250).Select(_ => TimeSpan.FromSeconds(delta.image))),
            batches: new ConcurrentQueue<TimeSpan>(Enumerable.Range(0, 5).Select(_ => TimeSpan.FromSeconds(delta.batch))));
        var frames = new ConcurrentQueue<Frame>();
        var icons = (
            components: new[] { Left, Right, Up, Down },
            queue: new ConcurrentQueue<Icon>(),
            map: new ConcurrentDictionary<int, (string description, int[] context)>());
        StartCoroutine(UpdateFrames());
        StartCoroutine(UpdateIcons());
        StartCoroutine(UpdateState());
        StartCoroutine(UpdateDelta());
        StartCoroutine(UpdateInput());
        StartCoroutine(UpdateDebug());

        foreach (var item in Wait(ReadOutput()))
        {
            Cursor.visible = Application.isEditor;
            yield return item;
        }

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
            while (true)
            {
                if (icons.queue.TryDequeue(out var icon))
                    foreach (var target in icons.components)
                        Load(memory, target, icon);
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
            WriteInput(version => frame with { Version = version, Empty = true });

            var hidden = 226f;
            var speed = 5f;
            var context = Array.Empty<int>();
            while (true)
            {
                var generate = _inputs.Space.Take();
                var task = Task.WhenAll(
                    UpdateIcon(Left, _inputs.Left.Take(), generate, position => position.With(x: 0f), position => position.With(x: -hidden), context),
                    UpdateIcon(Right, _inputs.Right.Take(), generate, position => position.With(x: 0f), position => position.With(x: hidden), context),
                    UpdateIcon(Up, _inputs.Up.Take(), generate, position => position.With(y: 0f), position => position.With(y: hidden), context),
                    UpdateIcon(Down, _inputs.Down.Take(), generate, position => position.With(y: 0f), position => position.With(y: -hidden), context));
                foreach (var item in Wait(task)) yield return item;
                yield return null;
            }

            async Task UpdateIcon(global::Icon component, bool move, bool generate, Func<Vector2, Vector2> show, Func<Vector2, Vector2> hide, int[] context)
            {
                var prompt = $"In a sequence of eccentric storytelling images, describe the next wildly surreal impossible creative image with themes loosely related to the color '{component.Color}' and vaguely inspired by [{string.Join(", ", component.Themes)}] and that follows from the previous description. Allow yourself to diverge creatively and explore niche subjects. Your answer must strictly consist of a short succinct summary image description; nothing else; no salutation, no introduction.";
                {
                    var source = component.Rectangle.anchoredPosition;
                    var target = move ? show(source) : hide(source);
                    var position = Vector2.Lerp(source, target, Time.deltaTime * speed);
                    component.Rectangle.anchoredPosition = position;
                }
                {
                    var alpha = move ? Mathf.Max(1f - component.Time, 0f) : 1f;
                    var source = component.Socket.Close.color;
                    var target = source.With(a: alpha);
                    var color = Color.Lerp(source, target, Time.deltaTime * speed);
                    component.Socket.Close.color = color;
                }
                {
                    var random = new Vector3(Random.value, Random.value) * 2.5f;
                    var shake = random * Mathf.Clamp(component.Time - 5f, 0f, 5f);
                    component.Shake.anchoredPosition = shake;
                }
                component.Time = move ? component.Time + Time.deltaTime : 0f;

                if (generate)
                {
                    var pair = await Generate(ollama.client, prompt, context);
                    var positive = $"(oil painting, vibrant, toon, surreal, close up, huge, simple, minimalistic, figurative) App icon: {pair.description}";
                    var version = WriteInput(version => new State()
                    {
                        Version = version,
                        Tags = Tags.Icon | component.Tags,
                        Load = $"{component.Color}.png".ToLowerInvariant(),
                        Positive = positive,
                        Width = 384,
                        Height = 384,
                        Loops = 0,
                        Steps = 10,
                        Guidance = 8f,
                        Denoise = 0.6f,
                        Full = false,
                        Break = false,
                    });
                    icons.map.TryAdd(version, pair);
                }

                if (component.Time >= 10f)
                {
                    Flash.color = Flash.color.With(a: 1f);
                    WriteInput(version => new[] { _resolutions.medium, _resolutions.low, _resolutions.low, _resolutions.low, _resolutions.medium }.Aggregate(
                        frame with { Version = version, Stop = true, Positive = component.Description },
                        (state, resolution) => frame with
                        {
                            Version = version,
                            Width = resolution.width,
                            Height = resolution.height,
                            Left = Math.Max(-component.Direction.x, 0),
                            Right = Math.Max(component.Direction.x, 0),
                            Top = Math.Max(component.Direction.y, 0),
                            Bottom = Math.Max(-component.Direction.y, 0),
                            Stop = true,
                            Positive = component.Description,
                            Next = state
                        }));
                }
            }
        }

        int WriteInput(Func<int, State> get)
        {
            var version = Interlocked.Increment(ref _version);
            var state = get(_version);
            Debug.Log($"COMFY: Sending input '{state}'.");
            comfy.StandardInput.WriteLine($"{state}");
            comfy.StandardInput.Flush();
            return version;
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
                    if (tags.HasFlag(Tags.Icon) && icons.map.TryRemove(version, out var pair))
                    {
                        icons.queue.Enqueue(new Icon
                        {
                            Version = version,
                            Width = width,
                            Height = height,
                            Offset = offset,
                            Size = size,
                            Tags = tags,
                            Description = pair.description,
                            Context = pair.context
                        });
                    }
                }
                catch (FormatException) { Debug.LogWarning($"COMFY: {line}"); }
                catch (IndexOutOfRangeException) { Debug.LogWarning($"COMFY: {line}"); }
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