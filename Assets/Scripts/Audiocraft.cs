using System.Diagnostics;
using System.IO.MemoryMappedFiles;

public static class Audiocraft
{
    public static (Process process, MemoryMappedFile memory) Create() => (Utility.Docker("audiocraft"), Utility.Memory("audiocraft"));
}