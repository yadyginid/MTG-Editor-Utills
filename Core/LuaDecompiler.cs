using System.Text;
using MoonSharp.Interpreter;

namespace UrukCardEditor.Core;

public static class LuaDecompiler
{
    public static bool IsCompiled(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 6 && bytes[0] == 0x1B && bytes[1] == (byte)'L' && bytes[2] == (byte)'u' && bytes[3] == (byte)'a';

    public static string Decode(byte[] bytes)
    {
        if (!IsCompiled(bytes))
            return Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Replace('\r', '\n');

        var patched = bytes.ToArray();
        patched[5] = 0;
        var buffer = new unluacNet.ByteBuffer(patched);
        var header = new unluac.parse.BHeader(buffer);
        var function = header.function.parse(buffer, header);
        var decompiler = new unluac.decompile.Decompiler(function);
        decompiler.decompile();
        using var writer = new StringWriter();
        decompiler.print(new WriterOutput(writer));
        return writer.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
    }

    public static void Validate(string source, string friendlyName = "card.lol")
    {
        var script = new Script(CoreModules.None);
        script.LoadString(source, null, friendlyName);
    }

    private sealed class WriterOutput(TextWriter writer) : unluac.decompile.OutputProvider
    {
        public void print(string text) => writer.Write(text);
        public void println() => writer.WriteLine();
    }
}
