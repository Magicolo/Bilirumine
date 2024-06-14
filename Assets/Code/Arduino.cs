#nullable enable

using System;
using System.Threading.Tasks;
using MonoSerialPort;

public sealed class Arduino
{
    public static void Log(string message) => Utility.Log(nameof(Arduino), message);
    public static void Warn(string message) => Utility.Warn(nameof(Arduino), message);
    public static void Error(string message) => Utility.Error(nameof(Arduino), message);
    public static void Except(Exception exception) => Utility.Except(nameof(Arduino), exception);

    bool _enable;
    readonly SerialPortInput? _serial;


    public Arduino()
    {
        try
        {
            var ports = SerialPortInput.GetPorts();
            Log($"Ports: {string.Join(", ", ports)}");
            _serial = Utility.Serial(ports[0], 9600);
        }
        catch (Exception exception) { Warn($"{exception}"); }
    }

    public bool Set(bool enable) => _enable.Change(enable);

    public async Task Read(bool[] inputs)
    {
        var read = new byte[inputs.Length * 2];
        var write = new byte[1];
        while (_serial is { IsConnected: true, Stream: var stream })
        {
            write[0] = _enable ? (byte)1 : (byte)0;
            await stream.WriteAsync(write);
            await stream.FlushAsync();
            if (await stream.ReadAsync(read) == 0) break;
            for (int i = 0; i < inputs.Length; i++) inputs[i] = read[i] > 0;
        }
    }
}