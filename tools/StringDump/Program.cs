using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// 从程序集 IL 里把 ldstr 的用户字符串全部倒出来（附带所在类型/方法）。
// 用途：源码被编码事故损坏时，从"事故前编译好的 DLL"里找回原始文案。
// 用法：dotnet run -- <assembly.dll> [filterSubstring]

var path = args.Length > 0 ? args[0] : throw new ArgumentException("need assembly path");
var filter = args.Length > 1 ? args[1] : null;

using var stream = File.OpenRead(path);
using var pe = new PEReader(stream);
var reader = pe.GetMetadataReader();

var seen = new HashSet<string>(StringComparer.Ordinal);

foreach (var th in reader.TypeDefinitions)
{
    var td = reader.GetTypeDefinition(th);
    var typeName = reader.GetString(td.Name);
    var ns = reader.GetString(td.Namespace);
    var fullType = string.IsNullOrEmpty(ns) ? typeName : ns + "." + typeName;

    foreach (var mh in td.GetMethods())
    {
        var md = reader.GetMethodDefinition(mh);
        if (md.RelativeVirtualAddress == 0) continue;

        var body = pe.GetMethodBody(md.RelativeVirtualAddress);
        var il = body.GetILContent();

        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x72) continue;                        // ldstr
            var token = il[i + 1] | (il[i + 2] << 8) | (il[i + 3] << 16) | (il[i + 4] << 24);
            if ((token & unchecked((int)0xFF000000)) != 0x70000000) continue;

            string value;
            try
            {
                value = reader.GetUserString(MetadataTokens.UserStringHandle(token & 0x00FFFFFF));
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrEmpty(value) || !seen.Add(value)) continue;
            if (filter != null && !value.Contains(filter, StringComparison.Ordinal) &&
                !fullType.Contains(filter, StringComparison.Ordinal)) continue;

            Console.WriteLine($"{fullType} :: {value.Replace("\r", "\\r").Replace("\n", "\\n")}");
        }
    }
}
