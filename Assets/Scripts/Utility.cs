#nullable enable

using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.IO.MemoryMappedFiles;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;
using System.Linq;

public static class Utility
{
    public static string Escape(this string value) => value.Replace(@"""", @"\""");
    public static string Sanitize(this string value) => value.Replace('\n', ' ').Replace('\r', ' ').Replace('(', '<').Replace(')', '>');

    public static MemoryMappedFile Memory(string name)
    {
        var file = $"bilirumine_{name}";
        var path = $"/dev/shm/{file}";
        var memory = MemoryMappedFile.CreateFromFile(path, FileMode.OpenOrCreate, file, int.MaxValue, MemoryMappedFileAccess.ReadWrite);
        Application.quitting += () => { try { memory.Dispose(); } catch { } };
        Application.quitting += () => { try { File.Delete(path); } catch { } };
        return memory;
    }

    public static SerialPort Serial(string name, int rate)
    {
        var port = new SerialPort(name, rate);
        Application.quitting += () => { try { port.Close(); } catch { } };
        port.Open();
        return port;
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
        Application.quitting += () => { try { process.Dispose(); } catch { } };
        Application.quitting += () => { try { Process.Start(new ProcessStartInfo("docker", $"compose --file '{path}' kill {service}")); } catch { } };
        return process;
    }

    public static HttpClient Client(string address)
    {
        var client = new HttpClient() { BaseAddress = new(address) };
        Application.quitting += () => { try { client.Dispose(); } catch { } };
        return client;
    }

    public static string Styles(params string[] styles) => Styles(1f, styles);
    public static string Styles(float strength, params string[] styles) => Styles(styles.Select(style => (style, strength)));
    public static string Styles(params (string style, float strength)[] styles) => Styles(styles.AsEnumerable());
    public static string Styles(IEnumerable<(string style, float strength)> styles) => string.Join(" ", styles.Select(pair => pair.strength == 1f ? $"({pair.style})" : $"({pair.style}:{pair.strength})"));

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