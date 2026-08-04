using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;

// 允许测试项目访问 internal 成员，用于单元测试
[assembly: InternalsVisibleTo("FreeWPFShell.Tests")]

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

[assembly: AssemblyTitle("FreeWPFShell")]              // 文件说明
[assembly: AssemblyDescription("开源免费的Windows SSH客户端")] // 这项通常不会显示在资源管理器里
[assembly: AssemblyProduct("FreeWPFShell")]            // 产品名称
[assembly: AssemblyCopyright("https://github.com/fsquirt")] // 版权
[assembly: AssemblyVersion("2.2.1.0")]                 // 程序集版本
[assembly: AssemblyFileVersion("2.2.1.0")]             // 文件版本