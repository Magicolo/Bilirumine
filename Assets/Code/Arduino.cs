#nullable enable

using System;
using MonoSerialPort;

public static class Arduino
{
    public static SerialPortInput? Create()
    {
        try
        {
            var ports = SerialPortInput.GetPorts();
            Log($"Ports: {string.Join(", ", ports)}");
            return ports.TryFirst(out var port) ? Utility.Serial(port, 9600) : null;
        }
        catch (Exception exception)
        {
            Warn($"{exception}");
            return null;
        }
    }

    public static void Log(string message) => Utility.Log(nameof(Arduino), message);
    public static void Warn(string message) => Utility.Warn(nameof(Arduino), message);
    public static void Error(string message) => Utility.Error(nameof(Arduino), message);
    public static void Except(Exception exception) => Utility.Except(nameof(Arduino), exception);
}