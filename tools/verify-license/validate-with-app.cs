// 通过反射加载生产程序集，用真实的 LicenseService.Validate() 校验真实配置目录中的 license.json
using System.Reflection;

// file-based 宿主默认禁用反射序列化；真实程序无此限制，这里显式打开以还原生产行为
AppContext.SetData("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", true);

var binDir = @"D:\AI执行软\vision-workbench\src\VisionWorkbench.App\bin\Debug\net10.0-windows";
var probeDirs = new[] { binDir }
    .Concat(Directory.GetDirectories(@"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App"))
    .Concat(Directory.GetDirectories(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App"))
    .Reverse()
    .ToArray();
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var name = new AssemblyName(e.Name).Name + ".dll";
    foreach (var dir in probeDirs)
    {
        var path = Path.Combine(dir, name);
        if (File.Exists(path)) return Assembly.LoadFrom(path);
    }
    return null;
};

var configDir = @"C:\ProgramData\VisionWorkbench\Config";
var appSettings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(binDir, "appsettings.json")));
var pubKey = appSettings.RootElement.GetProperty("App").GetProperty("LicensePublicKey").GetString();
Console.WriteLine($"公钥(来自exe目录appsettings): {pubKey![..50]}...");
Console.WriteLine($"license.json 存在: {File.Exists(Path.Combine(configDir, "license.json"))}");

var asm = Assembly.LoadFrom(Path.Combine(binDir, "VisionWorkbench.dll"));
var type = asm.GetType("VisionWorkbench.App.LicenseService") ?? throw new Exception("LicenseService type not found");
var ctor = type.GetConstructors().Single(c => c.GetParameters().Length == 3);
var instance = ctor.Invoke(new object?[] { configDir, pubKey, null });
var status = type.GetMethod("Validate")!.Invoke(instance, null);
foreach (var prop in status!.GetType().GetProperties())
{
    var value = prop.GetValue(status);
    Console.WriteLine(value is DateTime d ? $"{prop.Name}={d:O}" : $"{prop.Name}={value}");
}
