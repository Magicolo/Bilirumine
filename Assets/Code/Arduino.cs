#nullable enable

using System;
using System.Threading.Tasks;
using MonoSerialPort;

public sealed class Arduino
{
    public static Arduino Create()
    {
        try
        {
            var ports = SerialPortInput.GetPorts();
            Log($"Ports: {string.Join(", ", ports)}");
            return new(Utility.Serial(ports[0], 9600));
        }
        catch (Exception exception)
        {
            Warn($"{exception}");
            return new(null);
        }
    }

    public static void Log(string message) => Utility.Log(nameof(Arduino), message);
    public static void Warn(string message) => Utility.Warn(nameof(Arduino), message);
    public static void Error(string message) => Utility.Error(nameof(Arduino), message);
    public static void Except(Exception exception) => Utility.Except(nameof(Arduino), exception);

    readonly SerialPortInput? _serial;

    Arduino(SerialPortInput? serial) => _serial = serial;

    public async Task Read(bool[] inputs)
    {
        var index = 0;
        var buffer = new byte[32];
        while (_serial is { IsConnected: true, Stream: var stream })
        {
            var count = await stream.ReadAsync(buffer);
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] == byte.MaxValue) index = 0;
                else if (index < inputs.Length) inputs[index++] = buffer[i] > 0;
            }
        }
    }
}