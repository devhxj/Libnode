#:property PublishAot=false

using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

string? rid = null;
string? library = null;
var host = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--rid": rid = args[++i]; break;
        case "--library": library = args[++i]; break;
        case "--host": host = true; break;
        default: throw new ArgumentException("Use --rid <RID> --library <path>.");
    }
}
if (rid is not ("linux-x64" or "linux-arm64" or "win-x64" or "win-arm64"))
    throw new ArgumentException("Select linux-x64, linux-arm64, win-x64 or win-arm64 and pass --library.");
if (host)
{
    var hostRid = (OperatingSystem.IsWindows() ? "win-" : OperatingSystem.IsLinux() ? "linux-" : "unsupported-") +
        RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    if (hostRid != rid) throw new InvalidDataException($"Requested {rid}, but the .NET process runs as {hostRid}.");
    Console.WriteLine("Verified native runner architecture: " + hostRid);
    if (library is null) return;
}
if (library is null) throw new ArgumentException("Pass --library or --host.");
using var input = File.OpenRead(library);
if (rid.StartsWith("win-", StringComparison.Ordinal))
{
    using var pe = new PEReader(input);
    var expected = rid == "win-arm64" ? Machine.Arm64 : Machine.Amd64;
    if (pe.PEHeaders.CoffHeader.Machine != expected ||
        !pe.PEHeaders.CoffHeader.Characteristics.HasFlag(Characteristics.Dll))
        throw new InvalidDataException($"Native binary architecture does not match {rid} (expected a {expected} DLL).");
}
else
{
    var header = new byte[20];
    input.ReadExactly(header);
    var expected = rid == "linux-arm64" ? 183 : 62; // ELF EM_AARCH64 / EM_X86_64
    if (!header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' }) ||
        header[4] != 2 || header[5] != 1 ||
        BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16)) != 3 ||
        BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18)) != expected)
        throw new InvalidDataException($"Native binary architecture does not match {rid} (expected a 64-bit ELF shared library).");
}
Console.WriteLine($"Verified {rid} binary architecture: {library}");
