using System.Text.Json.Nodes;

namespace Insolvia.Forms.Tests.Support;

internal static class Parity
{
    public static void WritePacket(JsonObject input, string directory)
    {
        _ = input;
        System.IO.Directory.CreateDirectory(directory);
    }
}
