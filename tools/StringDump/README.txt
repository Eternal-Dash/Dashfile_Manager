StringDump：从编译好的 .NET 程序集里倒出字符串字面量。

用途：源码被编码事故/误操作破坏、又没有版本库时，用事故前的 DLL 找回界面文案。

用法：
  dotnet run --project tools/StringDump -- path/to/App.dll [过滤词]

原理：扫描每个方法体里的 IL `ldstr`，取 #US 堆里的用户字符串。
