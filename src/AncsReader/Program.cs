using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length == 0)
{
    Console.Error.WriteLine("AncsReader ancs-devices | ancs <paired BLE device id> [seconds=45] | ancs-selftest");
    return 2;
}
var json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
return await AncsProbe.Run(args, value => Console.WriteLine(JsonSerializer.Serialize(value, json)));
