#nullable enable

using System.IO.Ports;
using Debug = UnityEngine.Debug;

public static class Arduino
{
    public static SerialPort? Create()
    {
        var ports = SerialPort.GetPortNames();
        Log($"Ports: {string.Join(", ", ports)}");
        return ports.TryFirst(out var port) ? Utility.Serial(port, 9600) : null;
    }

    public static void Log(string message) => Debug.Log($"ARDUINO: {message}");
    public static void Warn(string message) => Debug.LogWarning($"ARDUINO: {message}");
    public static void Error(string message) => Debug.LogError($"ARDUINO: {message}");
}