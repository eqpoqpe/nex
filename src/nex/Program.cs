using System.ComponentModel;
using System.Diagnostics;

var rootCommand = new RootCommand("cargo-like example runner for .NET repos");

var rootOption = new Option<DirectoryInfo>("--root")
{
    Description =
        "Repository root (defaults to walking up from the current directory until an examples/ folder is found).",
    DefaultValueFactory = _ => new DirectoryInfo(Environment.CurrentDirectory),
};

var configurationOption = new Option<string>("--configuration")
{
    Description = "Build configuration passed to dotnet run.",
    DefaultValueFactory = _ => "Debug",
};

var frameworkOption = new Option<string?>("--framework")
{
    Description = "Target framework passed to dotnet run (optional).",
};

// nex run --example [name] -- [args...]
var runCommand = new Command("run", "Run an example.");

// Required option, but value is optional:
// - `nex run --example hello` => runs hello
// - `nex run --example` => prints list
// - `nex run` => error
var exampleOption = new Option<string?>(name: "--example", aliases: ["-e"])
{
    Description =
        "Example name (folder name under examples/ or project name; .NET 10+: can also be a single .cs file name). If omitted after `--example`, prints the list.",
    Required = true,
    Arity = ArgumentArity.ZeroOrOne,
};

var exampleArgs = new Argument<string[]>("args")
{
    Description = "Arguments passed to the example after `--`.",
    Arity = ArgumentArity.ZeroOrMore,
};

runCommand.Options.Add(rootOption);
runCommand.Options.Add(configurationOption);
runCommand.Options.Add(frameworkOption);
runCommand.Options.Add(exampleOption);
runCommand.Arguments.Add(exampleArgs);

runCommand.SetAction(
    async (parseResult, ct) =>
    {
        var root = ResolveRepoRoot(parseResult.GetValue(rootOption)!);
        var cfg = parseResult.GetValue(configurationOption)!;
        var fw = parseResult.GetValue(frameworkOption);
        var name = parseResult.GetValue(exampleOption); // string?
        var passThru = parseResult.GetValue(exampleArgs) ?? [];

        var dotnetMajor = GetDotNetSdkMajorVersion();
        var examples = DiscoverExamples(root, dotnetMajor);

        if (examples.Count == 0)
        {
            Console.Error.WriteLine(
                $"No examples found under: {Path.Combine(root.FullName, "examples")}"
            );
            return 1;
        }

        // User typed: nex run --example   (no value)
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("Available examples:");

            foreach (var kv in examples.OrderBy(k => k.Key))
                Console.WriteLine($"  {kv.Key}");
            return 0;
        }

        if (!examples.TryGetValue(name!, out var target))
        {
            // Allow case-insensitive match
            var match = examples.FirstOrDefault(kv =>
                string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)
            );

            if (!string.IsNullOrWhiteSpace(match.Key))
                target = match.Value;
            else
            {
                Console.Error.WriteLine($"Example '{name}' not found.");

                if (dotnetMajor > 0 && dotnetMajor < 10)
                {
                    Console.Error.WriteLine(
                        "Note: Single-file .cs examples require .NET SDK 10+ (file-based apps)."
                    );
                }

                Console.Error.WriteLine("Available examples:");
                foreach (var ex in examples.Keys.OrderBy(x => x))
                    Console.Error.WriteLine($"  {ex}");

                return 2;
            }
        }

        return await RunDotNetRunAsync(target, cfg, fw, passThru, ct);
    }
);

rootCommand.Subcommands.Add(runCommand);

ParseResult pr = rootCommand.Parse(args);
return await pr.InvokeAsync();

static int GetDotNetSdkMajorVersion()
{
    try
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--version");

        using var proc = Process.Start(psi);
        if (proc is null)
            return 0;

        var stdout = proc.StandardOutput.ReadToEnd().Trim();
        var stderr = proc.StandardError.ReadToEnd().Trim();

        proc.WaitForExit(2_000);

        var text = !string.IsNullOrWhiteSpace(stdout) ? stdout : stderr;
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        // Handles: "10.0.0", "10.0.0-preview.4.12345", etc.
        var token = text.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return int.TryParse(token, out var major) ? major : 0;
    }
    catch (Win32Exception)
    {
        // dotnet not found on PATH
        return 0;
    }
    catch
    {
        return 0;
    }
}

static DirectoryInfo ResolveRepoRoot(DirectoryInfo start)
{
    var current = start.Exists ? start : new DirectoryInfo(Environment.CurrentDirectory);

    while (current != null)
    {
        var examplesDir = Path.Combine(current.FullName, "examples");
        var examplesDirAlt = Path.Combine(current.FullName, "Examples");

        if (Directory.Exists(examplesDir) || Directory.Exists(examplesDirAlt))
            return current;

        current = current.Parent;
    }

    return new DirectoryInfo(Environment.CurrentDirectory);
}

static Dictionary<string, ExampleTarget> DiscoverExamples(DirectoryInfo repoRoot, int dotnetMajor)
{
    var result = new Dictionary<string, ExampleTarget>(StringComparer.Ordinal);

    string? examplesDir =
        Directory.Exists(Path.Combine(repoRoot.FullName, "examples"))
            ? Path.Combine(repoRoot.FullName, "examples")
        : Directory.Exists(Path.Combine(repoRoot.FullName, "Examples"))
            ? Path.Combine(repoRoot.FullName, "Examples")
        : null;

    if (examplesDir is null)
        return result;

    // 1) Project-based examples
    foreach (
        var csproj in Directory.EnumerateFiles(examplesDir, "*.csproj", SearchOption.AllDirectories)
    )
    {
        var dirName = new DirectoryInfo(Path.GetDirectoryName(csproj)!).Name;
        var fileName = Path.GetFileNameWithoutExtension(csproj);

        if (!result.ContainsKey(dirName))
            result[dirName] = new ExampleTarget(ExampleKind.Project, csproj);

        if (!result.ContainsKey(fileName))
            result[fileName] = new ExampleTarget(ExampleKind.Project, csproj);
    }

    // 2) .NET 10+ single-file examples: <name>.cs (only in dirs WITHOUT a csproj)
    if (dotnetMajor >= 10)
    {
        foreach (
            var cs in Directory.EnumerateFiles(examplesDir, "*.cs", SearchOption.AllDirectories)
        )
        {
            if (IsUnderBuildOutput(cs))
                continue;

            var dir = Path.GetDirectoryName(cs)!;

            // Skip .cs files that live inside a project-based example directory.
            if (Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly).Any())
                continue;

            var fileName = Path.GetFileNameWithoutExtension(cs);

            // Only add if no project example already claimed the same key.
            if (!result.ContainsKey(fileName))
                result[fileName] = new ExampleTarget(ExampleKind.File, cs);
        }
    }

    return result;
}

static bool IsUnderBuildOutput(string path)
{
    // Avoid picking up obj/bin files accidentally.
    var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return parts.Any(p =>
        p.Equals("bin", StringComparison.OrdinalIgnoreCase)
        || p.Equals("obj", StringComparison.OrdinalIgnoreCase)
    );
}

static async Task<int> RunDotNetRunAsync(
    ExampleTarget target,
    string configuration,
    string? framework,
    string[] passThroughArgs,
    CancellationToken ct
)
{
    var psi = new ProcessStartInfo("dotnet")
    {
        RedirectStandardInput = false,
        RedirectStandardOutput = false,
        RedirectStandardError = false,
        UseShellExecute = false,
    };

    // dotnet run [options] (<file.cs> | --project <csproj>) -- <args...>
    psi.ArgumentList.Add("run");
    psi.ArgumentList.Add("--configuration");
    psi.ArgumentList.Add(configuration);

    if (!string.IsNullOrWhiteSpace(framework))
    {
        psi.ArgumentList.Add("--framework");
        psi.ArgumentList.Add(framework);
    }

    switch (target.Kind)
    {
        case ExampleKind.Project:
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(target.Path);
            psi.WorkingDirectory = Path.GetDirectoryName(target.Path)!;
            break;

        case ExampleKind.File:
            // .NET 10+ file-based app
            psi.ArgumentList.Add(target.Path);
            psi.WorkingDirectory = Path.GetDirectoryName(target.Path)!;
            break;
    }

    psi.ArgumentList.Add("--");
    foreach (var a in passThroughArgs)
        psi.ArgumentList.Add(a);

    try
    {
        using var proc = Process.Start(psi);
        if (proc is null)
        {
            Console.Error.WriteLine(
                "Failed to start 'dotnet'. Is the .NET SDK installed and on PATH?"
            );
            return 127;
        }

        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }
    catch (Win32Exception)
    {
        Console.Error.WriteLine("Failed to start 'dotnet'. Is the .NET SDK installed and on PATH?");
        return 127;
    }
}

file enum ExampleKind
{
    Project,
    File,
}

file sealed record ExampleTarget(ExampleKind Kind, string Path);
