#nullable enable

using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using MonoSerialPort;
using UnityEngine;
using Debug = UnityEngine.Debug;
using System.Linq;
using System;
using MonoSerialPort.Port;

public static class Utility
{
    public static string Escape(this string value) => value.Replace(@"""", @"\""");
    public static string Sanitize(this string value) => value.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Replace('"', '\'').Replace('{', '<').Replace('}', '>').Replace('[', '<').Replace(']', '>').Replace('(', '<').Replace(')', '>');

    public static Memory Memory(string name)
    {
        Log(nameof(Memory), $"Creating memory '{name}'.");
        var memory = new Memory(name);
        Application.quitting += () => { try { memory.Dispose(); } catch { } };
        return memory;
    }

    public static SerialPortInput? Serial(string name, int rate)
    {
        try
        {
            Log(nameof(Serial), $"Connecting to serial port '{name}' with baud rate '{rate}'.");
            var port = new SerialPortInput(name, rate, Parity.None, 8, StopBits.One, Handshake.None, false, true);
            Application.quitting += () => { try { port.Disconnect(); } catch { } };
            if (port.Connect()) return port;
        }
        catch (Exception exception) { Except(nameof(Serial), exception); }
        return null;
    }

    public static Process Process(string name, string command, string arguments)
    {
        Log(nameof(Process), $"Starting process '{name}' with '{command} {arguments}'.");
        var process = System.Diagnostics.Process.Start(new ProcessStartInfo(command, arguments)
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
                Warn(name, line);
            }
            if (process.HasExited)
                Warn(name, $"Process exited with exit code '{process.ExitCode}' at '{process.ExitTime}'.");
        });
        return process;
    }

    public static Process Docker(string service)
    {
        static void Kill(string path, string service)
        {
            Log(nameof(Docker), $"Killing docker container service '{service}' with file '{path}'.");
            try
            {
                var process = System.Diagnostics.Process.Start(new ProcessStartInfo("docker", $"compose --file '{path}' kill {service}"));
                process.WaitForExit();
            }
            catch (Exception exception) { Except(nameof(Docker), exception); }
        }

        var path = Path.Join(Application.streamingAssetsPath, "docker-compose.yml");
        Kill(path, service);
        Log(nameof(Docker), $"Starting docker container service '{service}' with file '{path}'.");
        var process = Process(service, "docker", $"compose --file '{path}' run --service-ports --interactive --rm {service}");
        Application.quitting += () => Kill(path, service);
        return process;
    }

    public static HttpClient Client(string address)
    {
        Log(nameof(Client), $"Creating HTTP client with address '{address}'.");
        var client = new HttpClient() { BaseAddress = new(address) };
        Application.quitting += () => { try { client.Dispose(); } catch { } };
        return client;
    }

    public static Color Color(this Colors color, float gray = 0f) => UnityEngine.Color.Lerp(color switch
    {
        Colors.Green => UnityEngine.Color.green,
        Colors.White => UnityEngine.Color.white,
        Colors.Red => UnityEngine.Color.red,
        Colors.Yellow => UnityEngine.Color.yellow,
        _ => throw new InvalidOperationException(),
    }, UnityEngine.Color.gray, gray);

    public static void Or(bool[] left, bool[] right, bool[] result)
    {
        for (int i = 0; i < result.Length; i++)
            result[i] = left[i] || right[i];
    }

    public static bool Chain(AudioSource from, AudioSource to, float volume, float pitch, float fade)
    {
        if (from is { isPlaying: false })
        {
            to.volume = volume;
            to.pitch = pitch;
            if (to is { isPlaying: false }) to.Play();
            return true;
        }

        var remain = from.clip.length - from.time;
        var start = fade - remain;
        if (start > 0f)
        {
            var ratio = fade <= 0f ? 1f : Mathf.Clamp01(start / fade);
            from.volume = Mathf.Cos(ratio * Mathf.PI * 0.5f) * volume;
            to.volume = Mathf.Sin(ratio * Mathf.PI * 0.5f) * volume;

            if (to is { isPlaying: false })
            {
                to.Play();
                to.time = start;
            }
            if (ratio >= 1f)
            {
                from.volume = 0f;
                to.volume = volume;
                return true;
            }
        }
        else if (to.isPlaying) to.Stop();

        from.pitch = pitch;
        to.pitch = pitch;
        return false;
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

    public static void Log(string name, string message) => Debug.Log($"[{DateTime.Now}] {name.ToUpperInvariant()}(log): {message}".Truncate(5000));
    public static void Warn(string name, string message) => Debug.LogWarning($"[{DateTime.Now}] {name.ToUpperInvariant()}(warn): {message}".Truncate(5000));
    public static void Error(string name, string message) => Debug.LogError($"[{DateTime.Now}] {name.ToUpperInvariant()}(error): {message}".Truncate(5000));
    public static void Except(string name, Exception exception) => Debug.LogException(new Exception($"[{DateTime.Now}] {name.ToUpperInvariant()}(except): {exception.Message}".Truncate(5000), exception));
}