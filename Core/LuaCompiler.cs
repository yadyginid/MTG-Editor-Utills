using System.Diagnostics;
using System.Text;

namespace UrukCardEditor.Core;

public static class LuaCompiler
{
    public static void CompileToFile(string source, string outputPath, string logicalName)
    {
        var compilerPath = Path.Combine(AppContext.BaseDirectory, "tools", "mdluac51.exe");
        if (!File.Exists(compilerPath))
            throw new FileNotFoundException("The bundled Lua 5.1 compiler was not found.", compilerPath);

        var safeName = string.Concat(Path.GetFileName(logicalName)
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "card.lol";
        var tempRoot = Path.Combine(Path.GetTempPath(), "UrukCardEditor", "LuaCompile", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var inputName = safeName;
        var outputName = safeName + ".compiled";
        var inputPath = Path.Combine(tempRoot, inputName);
        var compiledPath = Path.Combine(tempRoot, outputName);

        try
        {
            File.WriteAllBytes(inputPath, EncodeDecompilerSource(source));
            var start = new ProcessStartInfo
            {
                FileName = compilerPath,
                WorkingDirectory = tempRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(inputName);
            start.ArgumentList.Add(outputName);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start the Lua 5.1 compiler.");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("Lua compilation took longer than 30 seconds.");
            }
            if (process.ExitCode != 0 || !File.Exists(compiledPath))
                throw new InvalidDataException($"The Lua compiler exited with code {process.ExitCode}: {standardError}{standardOutput}");

            var bytes = File.ReadAllBytes(compiledPath);
            if (bytes.Length < 12 || bytes[0] != 0x1B || bytes[1] != (byte)'L' || bytes[2] != (byte)'u' ||
                bytes[3] != (byte)'a' || bytes[4] != 0x51 || bytes[5] != 0x5F || bytes[7] != 4)
                throw new InvalidDataException("The compiler produced an incompatible Lua bytecode header.");

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)
                ?? throw new InvalidDataException("The output path has no parent directory."));
            File.WriteAllBytes(outputPath, bytes);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static byte[] EncodeDecompilerSource(string source)
    {
        // unluac.net exposes every raw string byte as U+0000..U+00FF, but its
        // printer also inserts a spurious "\\00" before bytes >= 0x80. Thus an
        // original UTF-8 sequence D0 90 is printed as "\\00Ð\\00\u0090".
        // Remove those artificial escapes, then map U+0000..U+00FF back to the
        // exact original bytes. Genuine new Unicode text entered in the editor
        // is encoded as UTF-8.
        var normalized = new StringBuilder(source.Length);
        for (var index = 0; index < source.Length; index++)
        {
            if (index + 3 < source.Length &&
                source[index] == '\\' && source[index + 1] == '0' && source[index + 2] == '0' &&
                source[index + 3] is >= '\u0080' and <= '\u00FF')
            {
                index += 2;
                continue;
            }
            normalized.Append(source[index]);
        }

        using var output = new MemoryStream(normalized.Length);
        var encoded = new byte[4];
        foreach (var rune in normalized.ToString().EnumerateRunes())
        {
            if (rune.Value <= byte.MaxValue)
            {
                output.WriteByte((byte)rune.Value);
                continue;
            }

            var length = rune.EncodeToUtf8(encoded);
            output.Write(encoded, 0, length);
        }
        return output.ToArray();
    }
}
