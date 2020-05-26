using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace CompileCsSample
{
    static class Program
    {
        private const string DotNetRoot = "/usr/local/share/dotnet/shared/Microsoft.NETCore.App/3.1.0";
        private const string PulumiRoot = "/Users/mikhailshilkov/go/src/github.com/pulumi";
        private static readonly string[] SystemAssemblies =
        {
            "Microsoft.CSharp.dll",
            "System.Collections.dll",
            "System.Collections.Immutable.dll",
            "System.IO.FileSystem.dll",
            "System.Private.CoreLib.dll",
            "System.Runtime.dll",
            "System.Text.Json.dll",
        };
        private static readonly (string, string, string)[] PulumiProviders =
        {
            ("pulumi-azure", "Pulumi.Azure.dll", "azurerm"),
            ("pulumi-azuread", "Pulumi.AzureAD.dll", "azuread"),
            ("pulumi-aws", "Pulumi.Aws.dll", "aws"),
            ("pulumi-gcp", "Pulumi.Gcp.dll", "google"),
            ("pulumi-random", "Pulumi.Random.dll", "random"),
            ("pulumi-tls", "Pulumi.Tls.dll", "tls"),
        };
        
        private static int ValidCount = 0;
        private static int InvalidCount = 0;
        private static int UnresolvedResourceCount = 0;
        private static int TodoCount = 0;
        private static int UnknownCount = 0;
        
        static void Main(string[] args)
        {
            var path = args[0]; // e.g. /Users/mikhailshilkov/go/src/github.com/pulumi/pulumi-gcp/sdk/dotnet/
            var files = Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories);
            foreach (var file in files)
                CompileFile(file);
            Console.WriteLine($"Valid: {ValidCount}, invalid: {InvalidCount}, todo: {TodoCount}, unresolved: {UnresolvedResourceCount}, unknown: {UnknownCount}");
        }
        static void CompileFile(string path)
        {
            var lines = File.ReadAllLines(path);
            var snippets = new List<string>();
            string? buffer = null;
            foreach (var line in lines)
            {
                if (line == "    /// ```csharp")
                {
                    buffer = "";
                }
                else if (buffer != null)
                {
                    if (line == "    /// ```")
                    {
                        snippets.Add(buffer);
                        buffer = null;
                    }
                    else
                        buffer += line.Substring(8).Replace("&gt;", ">").Replace("&lt;", "<") + "\n";
                }
            }

            foreach (var snippet in snippets)
            {
                if (snippet.Contains("TODO"))
                {
                    TodoCount++;
                    Console.WriteLine("TODO: " + path);
                }
                
                SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(snippet);

                string assemblyName = Path.GetRandomFileName();
                MetadataReference[] references =
                    SystemAssemblies.Select(a => $"{DotNetRoot}/{a}")
                    .Append($"{PulumiRoot}/pulumi/sdk/dotnet/Pulumi/bin/Debug/netcoreapp3.1/Pulumi.dll")
                    .Concat(PulumiProviders.Select(v => $"{PulumiRoot}/{v.Item1}/sdk/dotnet/bin/Debug/netcoreapp3.1/{v.Item2}"))
                    .Select(s => MetadataReference.CreateFromFile(s))
                    .ToArray();

                CSharpCompilation compilation = CSharpCompilation.Create(
                    assemblyName,
                    syntaxTrees: new[] {syntaxTree},
                    references: references,
                    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                using (var ms = new MemoryStream())
                {
                    EmitResult result = compilation.Emit(ms);

                    if (result.Success)
                        ValidCount++;
                    else
                    {
                        IEnumerable<Diagnostic> failures = result.Diagnostics.Where(diagnostic =>
                            diagnostic.IsWarningAsError ||
                            diagnostic.Severity == DiagnosticSeverity.Error);
                        InvalidCount++;

                        if (failures.Any(f => f.Id == "CS0103" && (
                            PulumiProviders.Any(v => f.GetMessage().StartsWith($"The name '{v.Item3}_"))
                            || f.GetMessage() == "The name 'data' does not exist in the current context"
                            || f.GetMessage() == "The name 'var' does not exist in the current context")))
                        {
                            UnresolvedResourceCount++;
                            continue;
                        }

                        UnknownCount++;
                        Console.WriteLine("==============================================");
                        Console.WriteLine(path);
                        foreach (Diagnostic diagnostic in failures)
                        {
                            Console.Error.WriteLine("{0}: {1}", diagnostic.Id, diagnostic.GetMessage());
                        }
                    }
                }
            }
        }
    }
}
